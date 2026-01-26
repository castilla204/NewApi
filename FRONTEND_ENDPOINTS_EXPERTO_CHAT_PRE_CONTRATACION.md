# 💬 Endpoints para Experto: Chat Pre-Contratación

## 📋 Resumen

Se han creado endpoints para que el **experto pueda ver y acceder** a las conversaciones previas a contratar donde los clientes le han escrito.

---

## 🆕 Nuevos Endpoints

### 1. `GET /api/Chat/pre-hire-conversations`

**Descripción:** Lista todas las conversaciones previas a contratar del experto (donde el experto es el destinatario).

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:** Ninguno (usa el userId del token)

**Respuesta exitosa (200):**
```json
[
  {
    "conversationId": 123,
    "searchServiceId": 456,
    "serviceName": "Reparación de ordenadores",
    "servicePrice": 50.00,
    "serviceImageUrl": "https://...",
    "clientId": 1,
    "clientName": "Juan Pérez",
    "clientProfilePictureUrl": "https://...",
    "lastMessage": {
      "id": 789,
      "content": "Hola, ¿estás disponible?",
      "sentAt": "2026-01-26T20:00:00Z",
      "senderId": 1,
      "senderName": "Juan Pérez",
      "isRead": false
    },
    "unreadCount": 2,
    "createdAt": "2026-01-26T19:00:00Z",
    "updatedAt": "2026-01-26T20:00:00Z"
  }
]
```

**Características:**
- ✅ Solo muestra conversaciones donde el usuario es el experto
- ✅ Solo muestra conversaciones previas (`SearchHireId == null`)
- ✅ Incluye último mensaje de cada conversación
- ✅ Incluye contador de mensajes no leídos
- ✅ Ordenado por fecha de actualización (más recientes primero)
- ✅ Incluye información del servicio y del cliente

---

### 2. `GET /api/Chat/conversation-by-service?searchServiceId={id}`

**Descripción:** Obtiene o crea una conversación previa. **Ahora también funciona para el experto**.

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:**
- `searchServiceId` (query, requerido): ID del servicio

**Comportamiento:**
- ✅ **Si el usuario es el cliente:** Crea/obtiene conversación previa
- ✅ **Si el usuario es el experto:** Accede a la conversación existente donde es el experto
- ✅ **Si no existe conversación y el usuario es experto:** No crea nueva (solo el cliente puede iniciar)

**Respuesta:** Igual que antes (ConversationDto completo con todos los mensajes)

---

## 🎨 Implementación Frontend

### Lista de Conversaciones del Experto

```typescript
// components/ExpertPreHireConversations.tsx
import { useQuery } from '@tanstack/react-query';

interface PreHireConversationSummary {
  conversationId: number;
  searchServiceId: number;
  serviceName: string;
  servicePrice: number;
  serviceImageUrl?: string;
  clientId?: number;
  clientName: string;
  clientProfilePictureUrl?: string;
  lastMessage?: {
    id: number;
    content: string;
    sentAt: string;
    senderId?: number;
    senderName: string;
    isRead: boolean;
  };
  unreadCount: number;
  createdAt: string;
  updatedAt: string;
}

export const ExpertPreHireConversations = ({ token }: { token: string }) => {
  const API_URL = process.env.REACT_APP_API_URL || 'https://tu-api.com';

  const { data: conversations, isLoading } = useQuery<PreHireConversationSummary[]>({
    queryKey: ['expert-pre-hire-conversations'],
    queryFn: async () => {
      const response = await fetch(`${API_URL}/api/Chat/pre-hire-conversations`, {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });

      if (!response.ok) {
        throw new Error('Error al obtener conversaciones');
      }

      return response.json();
    }
  });

  if (isLoading) {
    return <div>Cargando conversaciones...</div>;
  }

  if (!conversations || conversations.length === 0) {
    return (
      <div className="no-conversations">
        <p>No tienes conversaciones previas a contratar</p>
      </div>
    );
  }

  return (
    <div className="expert-conversations-list">
      <h2>💬 Conversaciones antes de contratar</h2>
      
      {conversations.map((conv) => (
        <div
          key={conv.conversationId}
          className="conversation-item"
          onClick={() => openConversation(conv.conversationId, conv.searchServiceId)}
        >
          {/* Foto del cliente */}
          <img
            src={conv.clientProfilePictureUrl || '/default-avatar.png'}
            alt={conv.clientName}
            className="client-avatar"
          />
          
          <div className="conversation-info">
            <div className="conversation-header">
              <h3>{conv.clientName}</h3>
              {conv.unreadCount > 0 && (
                <span className="unread-badge">{conv.unreadCount}</span>
              )}
            </div>
            
            <p className="service-name">{conv.serviceName}</p>
            <p className="service-price">{conv.servicePrice}€</p>
            
            {conv.lastMessage && (
              <p className="last-message">
                {conv.lastMessage.senderName}: {conv.lastMessage.content}
              </p>
            )}
            
            <span className="last-update">
              {new Date(conv.updatedAt).toLocaleDateString('es-ES')}
            </span>
          </div>
        </div>
      ))}
    </div>
  );
};
```

### Abrir Conversación desde la Lista

```typescript
// Función para abrir una conversación específica
const openConversation = (conversationId: number, searchServiceId: number) => {
  // Opción 1: Usar el conversationId directamente (si tienes endpoint por ID)
  // Opción 2: Usar el searchServiceId con el endpoint existente
  fetch(`${API_URL}/api/Chat/conversation-by-service?searchServiceId=${searchServiceId}`, {
    headers: {
      'Authorization': `Bearer ${token}`
    }
  })
  .then(res => res.json())
  .then(conversation => {
    // Abrir componente de chat con esta conversación
    setSelectedConversation(conversation);
  });
};
```

---

## 🔄 Flujo Completo para el Experto

### 1. Experto ve lista de conversaciones
```
GET /api/Chat/pre-hire-conversations
→ Lista todas las conversaciones previas donde es experto
→ Muestra: Cliente, Servicio, Último mensaje, No leídos
```

### 2. Experto hace clic en una conversación
```
GET /api/Chat/conversation-by-service?searchServiceId={id}
→ Obtiene conversación completa con todos los mensajes
→ Abre componente de chat
```

### 3. Experto envía mensaje
```
POST /api/Chat/message
→ Mensaje se guarda en la conversación previa
→ Supabase Realtime notifica al cliente
```

### 4. Cliente contrata el servicio
```
POST /api/SearchHire
→ Backend migra mensajes a conversación de SearchHire
→ Experto puede seguir chateando en el chat contratado
```

---

## 📊 Estructura de Datos

### `PreHireConversationSummaryDto`
```typescript
{
  conversationId: number;           // ID de la conversación
  searchServiceId: number;           // ID del servicio
  serviceName: string;               // Nombre del servicio
  servicePrice: number;              // Precio del servicio
  serviceImageUrl?: string;          // Imagen del servicio
  clientId?: number;                 // ID del cliente
  clientName: string;                // Nombre del cliente
  clientProfilePictureUrl?: string;  // Foto del cliente
  lastMessage?: {                    // Último mensaje
    id: number;
    content: string;
    sentAt: string;
    senderId?: number;
    senderName: string;
    isRead: boolean;
  };
  unreadCount: number;               // Mensajes no leídos
  createdAt: string;                 // Fecha de creación
  updatedAt: string;                 // Última actualización
}
```

---

## ✅ Características

- ✅ **Lista completa** de conversaciones previas del experto
- ✅ **Información del cliente** (nombre, foto)
- ✅ **Información del servicio** (nombre, precio, imagen)
- ✅ **Último mensaje** de cada conversación
- ✅ **Contador de no leídos** por conversación
- ✅ **Ordenado por actualización** (más recientes primero)
- ✅ **Acceso directo** a la conversación completa

---

## 🎯 Casos de Uso

### 1. Panel del Experto
```
Experto → Ve lista de conversaciones previas
→ Hace clic en una
→ Abre chat completo
→ Responde al cliente
```

### 2. Notificaciones
```
Cliente envía mensaje
→ Experto recibe notificación
→ Ve conversación en lista con badge de no leídos
→ Abre y responde
```

### 3. Seguimiento
```
Experto puede ver todas las personas que le han escrito
→ Puede responder a todas
→ Puede ver qué servicios generan más interés
```

---

## 📝 Notas Importantes

1. **Solo conversaciones previas:** El endpoint solo muestra conversaciones donde `SearchHireId == null`
2. **Solo del experto:** Solo muestra conversaciones donde el usuario es el experto
3. **Ordenado:** Por `UpdatedAt` descendente (más recientes primero)
4. **Último mensaje:** Solo incluye el último mensaje para optimizar
5. **No leídos:** Cuenta mensajes donde `IsRead == false` y `SenderId != userId`

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Implementado
