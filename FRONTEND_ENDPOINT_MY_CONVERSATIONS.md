# 💬 Endpoint: Mis Conversaciones (Cliente)

## 📋 Resumen

Nuevo endpoint que devuelve **todas las conversaciones del cliente** (pre y post contratación) en un formato unificado, similar a WhatsApp o Wallapop.

---

## 🆕 Endpoint

### `GET /api/Chat/my-conversations`

**Descripción:** Obtiene todas las conversaciones del cliente autenticado, incluyendo:
- ✅ Conversaciones **pre-contratación** (con personas que te han hablado pero no has contratado)
- ✅ Conversaciones **post-contratación** (de servicios que has contratado)

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:** Ninguno (usa el userId del token)

**Respuesta exitosa (200):**
```json
[
  {
    "conversationId": 123,
    "conversationType": "pre-hire",
    "createdAt": "2026-01-26T19:00:00Z",
    "updatedAt": "2026-01-26T20:00:00Z",
    "unreadCount": 2,
    "lastMessage": {
      "id": 789,
      "content": "Hola, ¿estás disponible?",
      "sentAt": "2026-01-26T20:00:00Z",
      "senderId": 2,
      "senderName": "Carlos García",
      "isRead": false
    },
    "expertId": 2,
    "expertName": "Carlos García",
    "expertProfilePictureUrl": "https://...",
    "searchServiceId": 456,
    "serviceName": "Reparación de ordenadores",
    "servicePrice": 50.00,
    "serviceImageUrl": "https://..."
  },
  {
    "conversationId": 124,
    "conversationType": "post-hire",
    "createdAt": "2026-01-25T10:00:00Z",
    "updatedAt": "2026-01-26T18:30:00Z",
    "unreadCount": 0,
    "lastMessage": {
      "id": 790,
      "content": "Perfecto, te envío el archivo",
      "sentAt": "2026-01-26T18:30:00Z",
      "senderId": 3,
      "senderName": "María López",
      "isRead": true
    },
    "expertId": 3,
    "expertName": "María López",
    "expertProfilePictureUrl": "https://...",
    "searchHireId": 789,
    "hireStatus": "InProgress",
    "hireStatusTranslated": "En Progreso",
    "hireCreatedAt": "2026-01-25T10:00:00Z",
    "hireAmount": 75.50,
    "hireBaseAmount": 62.40,
    "hireTaxAmount": 13.10,
    "searchServiceId": 457,
    "serviceName": "Clases de inglés",
    "servicePrice": 75.50,
    "serviceImageUrl": "https://...",
    "searchTitle": "Necesito clases de inglés",
    "searchDescription": "Busco profesor para clases particulares"
  }
]
```

---

## 📊 Estructura del DTO

### `ClientConversationSummaryDto`

```typescript
interface ClientConversationSummaryDto {
  // Información básica de la conversación
  conversationId: number;
  conversationType: "pre-hire" | "post-hire";
  createdAt: string;
  updatedAt: string;
  unreadCount: number;
  lastMessage: MessageSummaryDto | null;

  // Información del experto (común para ambos tipos)
  expertId: number | null;
  expertName: string;
  expertProfilePictureUrl: string | null;

  // Información para PRE-CONTRATACIÓN (solo si conversationType === "pre-hire")
  searchServiceId?: number | null;
  serviceName?: string | null;
  servicePrice?: number | null;
  serviceImageUrl?: string | null;

  // Información para POST-CONTRATACIÓN (solo si conversationType === "post-hire")
  searchHireId?: number | null;
  hireStatus?: string | null;
  hireStatusTranslated?: string | null;
  hireCreatedAt?: string | null;
  hireAmount?: number | null;
  hireBaseAmount?: number | null;
  hireTaxAmount?: number | null;
  searchTitle?: string | null;
  searchDescription?: string | null;
  // También incluye searchServiceId, serviceName, servicePrice, serviceImageUrl
}

interface MessageSummaryDto {
  id: number;
  content: string;
  sentAt: string;
  senderId: number | null;
  senderName: string;
  isRead: boolean;
}
```

---

## 🎯 Características

- ✅ **Listado unificado:** Todas las conversaciones en un solo endpoint
- ✅ **Ordenado por fecha:** Más recientes primero (por `updatedAt`)
- ✅ **Último mensaje:** Incluye el último mensaje de cada conversación
- ✅ **Contador de no leídos:** Muestra cuántos mensajes no has leído
- ✅ **Información completa:** Incluye datos del servicio y/o contratación
- ✅ **Tipo de conversación:** Identifica si es pre o post contratación
- ✅ **Información del experto:** Nombre, foto de perfil, etc.

---

## 💻 Ejemplo de Uso (Frontend)

### Con React Query

```typescript
import { useQuery } from '@tanstack/react-query';

interface ClientConversationSummaryDto {
  conversationId: number;
  conversationType: "pre-hire" | "post-hire";
  createdAt: string;
  updatedAt: string;
  unreadCount: number;
  lastMessage: MessageSummaryDto | null;
  expertId: number | null;
  expertName: string;
  expertProfilePictureUrl: string | null;
  // ... resto de campos
}

const useMyConversations = () => {
  return useQuery<ClientConversationSummaryDto[]>({
    queryKey: ['my-conversations'],
    queryFn: async () => {
      const response = await fetch(`${API_URL}/api/Chat/my-conversations`, {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });

      if (!response.ok) {
        throw new Error('Error al obtener conversaciones');
      }

      return response.json();
    },
    staleTime: 30000, // 30 segundos
    refetchInterval: 60000 // Refrescar cada minuto
  });
};

// Uso en componente
const ConversationsList = () => {
  const { data: conversations, isLoading } = useMyConversations();

  if (isLoading) return <div>Cargando...</div>;

  return (
    <div className="conversations-list">
      {conversations?.map(conv => (
        <ConversationCard 
          key={conv.conversationId} 
          conversation={conv}
        />
      ))}
    </div>
  );
};
```

### Componente de Tarjeta de Conversación

```typescript
const ConversationCard = ({ conversation }: { conversation: ClientConversationSummaryDto }) => {
  const isPreHire = conversation.conversationType === "pre-hire";
  
  return (
    <div className="conversation-card">
      {/* Imagen del servicio o experto */}
      <img 
        src={conversation.serviceImageUrl || conversation.expertProfilePictureUrl || '/default.png'} 
        alt={conversation.serviceName || conversation.expertName}
      />
      
      {/* Información principal */}
      <div className="conversation-info">
        <h3>
          {isPreHire 
            ? conversation.serviceName || conversation.expertName
            : conversation.searchTitle || conversation.serviceName || conversation.expertName
          }
        </h3>
        
        <p className="expert-name">{conversation.expertName}</p>
        
        {/* Último mensaje */}
        {conversation.lastMessage && (
          <p className="last-message">{conversation.lastMessage.content}</p>
        )}
        
        {/* Estado de contratación (solo post-hire) */}
        {!isPreHire && conversation.hireStatusTranslated && (
          <span className="hire-status">{conversation.hireStatusTranslated}</span>
        )}
      </div>
      
      {/* Badge de no leídos */}
      {conversation.unreadCount > 0 && (
        <span className="unread-badge">{conversation.unreadCount}</span>
      )}
      
      {/* Timestamp */}
      <span className="timestamp">
        {formatRelativeTime(conversation.lastMessage?.sentAt || conversation.updatedAt)}
      </span>
    </div>
  );
};
```

---

## 🔗 Navegación a Chat

Para abrir el chat desde una conversación:

### Pre-Contratación:
```typescript
// Navegar a: /chat-pre-contratacion/{searchServiceId}
router.push(`/chat-pre-contratacion/${conversation.searchServiceId}`);
```

### Post-Contratación:
```typescript
// Navegar a: /chat/{searchHireId} o usar el endpoint by-searchhire
router.push(`/chat/${conversation.searchHireId}`);
```

---

## ⚠️ Errores Posibles

- `401 Unauthorized`: Token inválido o faltante
- `500 Internal Server Error`: Error en el servidor

---

## 📝 Notas

- ✅ **No modifica** el endpoint `/api/Search/{searchId}/details-complete`
- ✅ Las conversaciones están **ordenadas por fecha de actualización** (más recientes primero)
- ✅ El contador de no leídos solo cuenta mensajes donde el remitente **no es el usuario actual**
- ✅ Para conversaciones post-contratación, también se incluye `searchServiceId` para facilitar la navegación

---

**Fecha:** 2026-01-27  
**Estado:** ✅ Implementado y listo para usar
