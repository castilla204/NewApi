# 🚀 **GUÍA FRONTEND - ENDPOINT DETAILS-COMPLETE ACTUALIZADO**

## 📋 **CAMBIOS REALIZADOS**

### **❌ ENDPOINT ELIMINADO:**
- `GET /api/Search/{searchId}/details-additional` - **YA NO EXISTE**

### **✅ ENDPOINT ACTUALIZADO:**
- `GET /api/Search/{searchId}/details-complete` - **AHORA INCLUYE TODO**

---

## 🎯 **NUEVA ESTRUCTURA DE RESPUESTA**

### **Endpoint:** `GET /api/Search/{searchId}/details-complete`

**Respuesta:** `SearchDetailsCompleteResponseDto`

```typescript
interface SearchDetailsCompleteResponseDto {
  search: SearchListDto;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  disputes: DisputeDto[];
}
```

---

## 📊 **DTOs UTILIZADOS**

### **1. SearchListDto**
```typescript
interface SearchListDto {
  id: number;
  userId: number;
  title: string;
  description: string;
  frequency: string;
  isActive: boolean;
  isRevised: boolean;
  createdAt: string;
  user: UserDto;
  searchHire: SearchHireDto | null;
}

interface UserDto {
  id: number;
  name: string;
  email: string;
  profilePictureUrl: string | null;
}

interface SearchHireDto {
  id: number;
  status: string;
  createdAt: string;
  expert: UserDto | null;
  service: ServiceInfo | null;
}

interface ServiceInfo {
  id: number;
  serviceTypeId: number;
  serviceTypeName: string;
  serviceTypeCategoryId: number | null;
  serviceTypeCategoryName: string | null;
  requiresAppointment: boolean;
  price: number;
}
```

### **2. MoneyDistributionConfigDto**
```typescript
interface MoneyDistributionConfigDto {
  clientPercentage: number;
  expertPercentage: number;
  platformPercentage: number;
  source: string;
  status: string;
}
```

### **3. CategoryDto** ⭐ **NUEVO**
```typescript
interface CategoryDto {
  id: number;
  name: string;
  parentId: number | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}
```

### **4. ReviewDto** ⭐ **NUEVO**
```typescript
interface ReviewDto {
  id: number;
  score: number;                    // Puntuación 1-5
  description: string;              // Descripción de la reseña
  createdAt: string;                // Fecha de creación
  reviewer: UserDto;               // Información del revisor
  imageUrls: string[];             // URLs de las imágenes de la reseña
}
```

### **5. AppointmentDto** ⭐ **MOVIDO DESDE DETAILS-ADDITIONAL**
```typescript
interface AppointmentDto {
  id: number;
  searchHireId: number;
  status: string;
  proposedDate: string;
  proposedTime: string;
  location: string;
  latitude: number | null;
  longitude: number | null;
  doorNumber: string | null;
  ownerPhone: string | null;
  siteDetails: string | null;
  disputeReason: string | null;
  completedAt: string | null;
  completedBy: number | null;
  rejectionCount: number;
  cancellationCount: number;
  lastRejectionAt: string | null;
  lastProposalAt: string | null;
  lastResponseAt: string | null;
  isLocked: boolean;
  createdAt: string;
  updatedAt: string;
  clientName: string | null;
  expertName: string | null;
  amount: number;
  timers: AppointmentTimerDto[];
}

interface AppointmentTimerDto {
  id: number;
  appointmentId: number;
  timerType: string;
  startTime: string;
  endTime: string | null;
  isExpired: boolean;
  expiredAt: string | null;
}
```

### **6. DeliverableDto** ⭐ **MOVIDO DESDE DETAILS-ADDITIONAL**
```typescript
interface DeliverableDto {
  id: number;
  type: string;                    // "pdf", "image", etc.
  url: string;                     // URL del archivo
  createdAt: string;
}
```

### **7. DisputeDto** ⭐ **MOVIDO DESDE DETAILS-ADDITIONAL**
```typescript
interface DisputeDto {
  id: number;
  searchHireId: number;
  reporterId: number;
  status: string;
  reason: string;
  expertResponse: string | null;
  createdAt: string;
}
```

---

## 🔧 **IMPLEMENTACIÓN FRONTEND**

### **1. Hook Actualizado:**
```typescript
// src/hooks/useSearchDetailsComplete.ts
import { useQuery } from '@tanstack/react-query';
import { fetchApi } from '../utils/api';

export interface SearchDetailsCompleteResponseDto {
  search: SearchListDto;
  moneyDistribution: MoneyDistributionConfigDto | null;
  category: CategoryDto | null;
  review: ReviewDto | null;
  appointment: AppointmentDto | null;
  deliverables: DeliverableDto[];
  disputes: DisputeDto[];
}

export const useSearchDetailsComplete = (searchId: number) => {
  return useQuery({
    queryKey: ['searchDetailsComplete', searchId],
    queryFn: () => fetchApi<SearchDetailsCompleteResponseDto>(`/api/Search/${searchId}/details-complete`),
    staleTime: 30000, // 30 segundos de cache
    cacheTime: 300000, // 5 minutos en cache
    enabled: !!searchId
  });
};
```

### **2. Uso en Componente:**
```typescript
// En tu componente SearchDetails
const { data: searchDetails, isLoading, error } = useSearchDetailsComplete(searchId);

if (isLoading) return <LoadingSpinner />;
if (error) return <ErrorMessage error={error} />;
if (!searchDetails) return <NotFound />;

// Ahora tienes acceso a TODOS los datos en una sola respuesta:
const {
  search,           // Datos básicos de la búsqueda
  moneyDistribution, // Configuración de dinero
  category,         // ⭐ NUEVO: Categoría del servicio
  review,           // ⭐ NUEVO: Reseña si existe
  appointment,      // ⭐ MOVIDO: Datos de la cita
  deliverables,     // ⭐ MOVIDO: Archivos subidos
  disputes          // ⭐ MOVIDO: Disputas
} = searchDetails;

// Ejemplo de uso:
return (
  <div>
    <h1>{search.title}</h1>
    <p>Categoría: {category?.name}</p>
    
    {review && (
      <div>
        <h3>Reseña</h3>
        <p>Puntuación: {review.score}/5</p>
        <p>Descripción: {review.description}</p>
        <p>Revisor: {review.reviewer.name}</p>
        {review.imageUrls.map(url => (
          <img key={url} src={url} alt="Reseña" />
        ))}
      </div>
    )}
    
    {appointment && (
      <div>
        <h3>Cita</h3>
        <p>Fecha: {appointment.proposedDate}</p>
        <p>Hora: {appointment.proposedTime}</p>
        <p>Ubicación: {appointment.location}</p>
      </div>
    )}
    
    {deliverables.length > 0 && (
      <div>
        <h3>Archivos Entregados</h3>
        {deliverables.map(deliverable => (
          <a key={deliverable.id} href={deliverable.url} target="_blank">
            {deliverable.type} - {deliverable.createdAt}
          </a>
        ))}
      </div>
    )}
  </div>
);
```

---

## 🚀 **OPTIMIZACIÓN LOGRADA**

### **❌ ANTES (Múltiples llamadas):**
```typescript
// Necesitabas hacer múltiples llamadas:
const searchQuery = useSearchDetailsComplete(searchId);     // 1
const additionalQuery = useSearchDetailsAdditional(searchId); // 2 (ELIMINADO)
const categoryQuery = useCategories();                      // 3 (YA NO NECESARIO)
const conversationQuery = useConversation(searchId);        // 4 (Mantener)
```

### **✅ DESPUÉS (Una sola llamada):**
```typescript
// Ahora solo necesitas:
const searchQuery = useSearchDetailsComplete(searchId);     // 1 - TODO INCLUIDO
const conversationQuery = useConversation(searchId);        // 2 - Solo conversaciones
```

---

## 📝 **MIGRACIÓN REQUERIDA**

### **1. Eliminar Hooks Obsoletos:**
```typescript
// ❌ ELIMINAR ESTOS HOOKS:
// - useSearchDetailsAdditional
// - useCategories (para obtener categoría específica)
```

### **2. Actualizar Imports:**
```typescript
// ✅ ACTUALIZAR IMPORTS:
import { useSearchDetailsComplete } from '../hooks/useSearchDetailsComplete';
// Eliminar: import { useSearchDetailsAdditional } from '../hooks/useSearchDetailsAdditional';
```

### **3. Actualizar Queries:**
```typescript
// ❌ ANTES:
const { data: searchData } = useSearchDetailsComplete(searchId);
const { data: additionalData } = useSearchDetailsAdditional(searchId);

// ✅ DESPUÉS:
const { data: searchData } = useSearchDetailsComplete(searchId);
// additionalData ya no es necesario - todo está en searchData
```

---

## 🎯 **BENEFICIOS**

1. **🚀 Rendimiento**: Reducción de 3-4 llamadas API a solo 1
2. **📦 Datos Completos**: Toda la información en una sola respuesta
3. **🔧 Mantenibilidad**: Código más simple y fácil de mantener
4. **⚡ Velocidad**: Carga más rápida de la página de detalles
5. **🎨 UX Mejorada**: Menos estados de carga, experiencia más fluida

---

---

## 💬 **ENDPOINT DE CHAT PARA ARCHIVOS Y UBICACIÓN**

### **📤 Enviar Mensaje con Archivos y Ubicación**
**Endpoint:** `POST /api/chat/message`

**Formato:** `multipart/form-data` (FormData)

```typescript
// Función para enviar mensaje con archivos y ubicación
const sendMessageWithFiles = async (
  conversationId: number, 
  files: File[], 
  location?: {lat: number, lon: number},
  content?: string
) => {
  const formData = new FormData();
  
  // Datos básicos del mensaje
  formData.append('conversationId', conversationId.toString());
  
  // Contenido del mensaje (opcional)
  if (content) {
    formData.append('content', content);
  }
  
  // Ubicación (opcional)
  if (location) {
    formData.append('locationLatitude', location.lat.toString());
    formData.append('locationLongitude', location.lon.toString());
  }
  
  // Archivos - CLAVE: usar 'attachments' como nombre del campo
  files.forEach(file => {
    formData.append('attachments', file);
  });
  
  try {
    const response = await fetch('/api/chat/message', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`
        // NO pongas Content-Type, el navegador lo hace automáticamente para FormData
      },
      body: formData
    });
    
    if (!response.ok) {
      throw new Error(`Error ${response.status}: ${response.statusText}`);
    }
    
    const result = await response.json();
    console.log('Mensaje enviado:', result);
    return result;
  } catch (error) {
    console.error('Error enviando mensaje:', error);
    throw error;
  }
};
```

### **📋 Tipos de Archivo Soportados**

| Tipo | Extensión | Procesamiento |
|------|-----------|---------------|
| **Imágenes** | `.jpg`, `.jpeg`, `.png` | ✅ Redimensionado automático a 800x600 |
| **Videos** | `.mp4` | ❌ Sin procesamiento |
| **Otros** | Cualquier tipo | ❌ Sin procesamiento |

### **🎯 Ejemplos de Uso**

#### **1. Enviar Solo Archivos:**
```typescript
const files = document.getElementById('fileInput').files;
await sendMessageWithFiles(conversationId, Array.from(files));
```

#### **2. Enviar Solo Ubicación:**
```typescript
const location = { lat: 40.4168, lon: -3.7038 };
await sendMessageWithFiles(conversationId, [], location, "Mi ubicación actual");
```

#### **3. Enviar Todo Junto:**
```typescript
const files = document.getElementById('fileInput').files;
const location = { lat: 40.4168, lon: -3.7038 };
await sendMessageWithFiles(
  conversationId, 
  Array.from(files), 
  location, 
  "Archivos desde mi ubicación"
);
```

### **📥 Respuesta del Endpoint**

```typescript
interface MessageResponse {
  id: number;
  conversationId: number;
  senderId: number;
  content: string;
  sentAt: string;
  isRead: boolean;
  senderName: string;
  locationLatitude?: string;          // Ubicación si se envió
  locationLongitude?: string;         // Ubicación si se envió
  attachmentUrls: string[];          // URLs de archivos en Google Cloud Storage
}

// Ejemplo de respuesta:
{
  "id": 456,
  "conversationId": 123,
  "senderId": 789,
  "content": "Archivos desde mi ubicación",
  "sentAt": "2025-01-17T10:30:00Z",
  "isRead": false,
  "senderName": "Juan Pérez",
  "locationLatitude": "40.4168",
  "locationLongitude": "-3.7038",
  "attachmentUrls": [
    "https://storage.googleapis.com/tu-bucket/chat-attachments/123/guid1.jpg",
    "https://storage.googleapis.com/tu-bucket/chat-attachments/123/guid2.mp4"
  ]
}
```

### **🔧 Hook Personalizado para Chat**

```typescript
// src/hooks/useChatMessage.ts
import { useMutation } from '@tanstack/react-query';

interface SendMessageParams {
  conversationId: number;
  files?: File[];
  location?: { lat: number; lon: number };
  content?: string;
}

export const useSendMessage = () => {
  return useMutation({
    mutationFn: async ({ conversationId, files = [], location, content }: SendMessageParams) => {
      const formData = new FormData();
      
      formData.append('conversationId', conversationId.toString());
      
      if (content) {
        formData.append('content', content);
      }
      
      if (location) {
        formData.append('locationLatitude', location.lat.toString());
        formData.append('locationLongitude', location.lon.toString());
      }
      
      files.forEach(file => {
        formData.append('attachments', file);
      });
      
      const response = await fetch('/api/chat/message', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`
        },
        body: formData
      });
      
      if (!response.ok) {
        throw new Error(`Error ${response.status}`);
      }
      
      return response.json();
    },
    onSuccess: (data) => {
      console.log('Mensaje enviado:', data);
      // El SignalR se encarga de notificar en tiempo real
    },
    onError: (error) => {
      console.error('Error enviando mensaje:', error);
    }
  });
};

// Uso en componente:
const sendMessage = useSendMessage();

const handleSendFiles = () => {
  const files = fileInputRef.current?.files;
  const location = currentLocation; // Obtenido del GPS
  
  sendMessage.mutate({
    conversationId: 123,
    files: files ? Array.from(files) : [],
    location: location,
    content: "Archivos adjuntos"
  });
};
```

### **⚠️ Validaciones del Backend**

- **Ubicación**: Latitud (-90 a 90), Longitud (-180 a 180)
- **Archivos**: Máximo tamaño según configuración del servidor
- **Conversación**: Debe existir y el usuario debe tener acceso
- **Al menos uno**: Debe enviar contenido, archivos o ubicación

---

## ⚠️ **IMPORTANTE**

- **El endpoint `/api/Search/{searchId}/details-additional` ya NO EXISTE**
- **Toda la funcionalidad se ha movido a `/api/Search/{searchId}/details-complete`**
- **Las conversaciones siguen obteniéndose del endpoint `/api/chat/conversation?searchId={searchId}`**
- **La categoría ahora viene incluida en la respuesta principal (no necesitas llamar a `/api/categories`)**
- **Para archivos y ubicación en chat: usar `POST /api/chat/message` con FormData**
