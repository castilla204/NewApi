# 💬 Guía Frontend: Chat Pre-Contratación

## 📋 Resumen

Se ha implementado una nueva funcionalidad que permite a los usuarios **chatear con el experto antes de contratar** un servicio. Los mensajes enviados en este chat previo se **migran automáticamente** al chat del servicio contratado cuando se realiza la contratación.

---

## 🆕 Nuevos Endpoints

### 1. `GET /api/Chat/conversation-by-service?searchServiceId={id}`

**Para Cliente y Experto**

**Descripción:** Obtiene o crea una conversación previa a contratar para un servicio específico.

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:**
- `searchServiceId` (query, requerido): ID del servicio

**Respuesta exitosa (200):**
```json
{
  "id": 123,
  "searchHireId": null,           // ✅ null para conversaciones previas
  "searchServiceId": 456,         // ✅ ID del servicio
  "clientId": 1,
  "expertId": 2,
  "isActive": true,
  "createdAt": "2026-01-26T19:53:42Z",
  "updatedAt": "2026-01-26T19:53:42Z",
  "messages": [
    {
      "id": 1,
      "conversationId": 123,
      "senderId": 1,
      "content": "Hola, ¿estás disponible?",
      "sentAt": "2026-01-26T20:00:00Z",
      "isRead": false,
      "senderName": "Juan Pérez",
      "locationLatitude": null,
      "locationLongitude": null,
      "attachmentUrls": []
    }
  ]
}
```

**Comportamiento:**
- **Si el usuario es el cliente:**
  - Si existe conversación previa, la devuelve
  - Si no existe, crea una nueva conversación previa
- **Si el usuario es el experto:**
  - Si existe conversación previa, la devuelve (puede acceder y responder)
  - Si no existe, retorna 404 (solo el cliente puede iniciar conversaciones)

**Errores posibles:**
- `401 Unauthorized`: Token inválido o faltante
- `404 Not Found`: Servicio no encontrado o conversación no encontrada (si es experto)

---

### 2. `GET /api/Chat/pre-hire-conversations`

**Solo para Experto**

**Descripción:** Lista todas las conversaciones previas a contratar donde el experto es el destinatario. Permite al experto ver quién le ha hablado antes de contratar.

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

## 🎨 Implementación del Componente

### 1. Componente de Chat Pre-Contratación

```typescript
// components/PreHireChat.tsx
import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { supabase } from '@/lib/supabase';

interface PreHireChatProps {
  serviceId: number;
  token: string;
  userId: number;
  onClose?: () => void;
}

interface Message {
  id: number;
  conversationId: number;
  senderId: number | null;
  content: string;
  sentAt: string;
  isRead: boolean;
  senderName: string;
  attachmentUrls: string[];
}

interface Conversation {
  id: number;
  searchHireId: number | null;
  searchServiceId: number | null;
  clientId: number | null;
  expertId: number | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  messages: Message[];
}

export const PreHireChat = ({ serviceId, token, userId, onClose }: PreHireChatProps) => {
  const [inputValue, setInputValue] = useState('');
  const [isTyping, setIsTyping] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const API_URL = process.env.REACT_APP_API_URL || 'https://tu-api.com';

  // Obtener o crear conversación previa
  const { data: conversation, isLoading } = useQuery<Conversation>({
    queryKey: ['pre-hire-conversation', serviceId],
    queryFn: async () => {
      const response = await fetch(
        `${API_URL}/api/Chat/conversation-by-service?searchServiceId=${serviceId}`,
        {
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          }
        }
      );

      if (!response.ok) {
        throw new Error('Error al obtener conversación');
      }

      return response.json();
    },
    enabled: !!serviceId && !!token
  });

  // Estado local de mensajes (se actualiza con Supabase Realtime)
  const [messages, setMessages] = useState<Message[]>([]);

  // Inicializar mensajes desde la conversación
  useEffect(() => {
    if (conversation?.messages) {
      setMessages(conversation.messages);
    }
  }, [conversation]);

  // ✅ Suscribirse a nuevos mensajes con Supabase Realtime
  useEffect(() => {
    if (!conversation?.id) return;

    const channelName = `messages:conversation:${conversation.id}`;
    
    const channel = supabase
      .channel(channelName)
      .on(
        'postgres_changes',
        {
          event: 'INSERT',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversation.id}`
        },
        (payload) => {
          const newMessage = payload.new as any;
          // Convertir formato de BD a formato del frontend
          const messageDto: Message = {
            id: newMessage.Id,
            conversationId: newMessage.ConversationId,
            senderId: newMessage.SenderId,
            content: newMessage.Content || '',
            sentAt: newMessage.SentAt,
            isRead: newMessage.IsRead,
            senderName: '', // Se obtiene del sender
            attachmentUrls: []
          };
          
          setMessages(prev => {
            // Evitar duplicados
            if (prev.some(m => m.id === messageDto.id)) {
              return prev;
            }
            return [...prev, messageDto];
          });
        }
      )
      .on(
        'postgres_changes',
        {
          event: 'UPDATE',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversation.id}`
        },
        (payload) => {
          const updatedMessage = payload.new as any;
          setMessages(prev =>
            prev.map(msg =>
              msg.id === updatedMessage.Id
                ? {
                    ...msg,
                    isRead: updatedMessage.IsRead,
                    content: updatedMessage.Content || msg.content
                  }
                : msg
            )
          );
        }
      )
      .subscribe((status) => {
        console.log(`📡 Estado de suscripción: ${status}`);
      });

    return () => {
      supabase.removeChannel(channel);
    };
  }, [conversation?.id]);

  // Scroll automático al final
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Enviar mensaje
  const sendMessageMutation = useMutation({
    mutationFn: async (content: string) => {
      const formData = new FormData();
      formData.append('ConversationId', conversation!.id.toString());
      formData.append('Content', content);

      const response = await fetch(`${API_URL}/api/Chat/message`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`
        },
        body: formData
      });

      if (!response.ok) {
        throw new Error('Error al enviar mensaje');
      }

      return response.json();
    },
    onSuccess: () => {
      setInputValue('');
      setIsTyping(false);
    }
  });

  const handleSend = () => {
    if (!inputValue.trim() || !conversation) return;
    sendMessageMutation.mutate(inputValue.trim());
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  // Indicador de typing (opcional)
  useEffect(() => {
    let timeout: NodeJS.Timeout;
    
    if (inputValue.length > 0 && !isTyping) {
      setIsTyping(true);
      // Notificar typing
      fetch(`${API_URL}/api/Chat/typing`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          conversationId: conversation?.id,
          isTyping: true
        })
      });
    }
    
    timeout = setTimeout(() => {
      if (isTyping) {
        setIsTyping(false);
        fetch(`${API_URL}/api/Chat/typing`, {
          method: 'POST',
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({
            conversationId: conversation?.id,
            isTyping: false
          })
        });
      }
    }, 2000);

    return () => clearTimeout(timeout);
  }, [inputValue, conversation?.id, token, isTyping]);

  if (isLoading) {
    return (
      <div className="pre-hire-chat-loading">
        <p>Cargando conversación...</p>
      </div>
    );
  }

  if (!conversation) {
    return (
      <div className="pre-hire-chat-error">
        <p>No se pudo cargar la conversación</p>
      </div>
    );
  }

  const otherUserId = conversation.clientId === userId 
    ? conversation.expertId 
    : conversation.clientId;

  return (
    <div className="pre-hire-chat">
      {/* Header */}
      <div className="pre-hire-chat-header">
        <h3>💬 Chat antes de contratar</h3>
        {onClose && (
          <button onClick={onClose} className="close-button">
            ✕
          </button>
        )}
      </div>

      {/* Lista de mensajes */}
      <div className="pre-hire-chat-messages">
        {messages.length === 0 ? (
          <div className="no-messages">
            <p>No hay mensajes aún. ¡Empieza la conversación!</p>
          </div>
        ) : (
          messages.map((message) => {
            const isOwnMessage = message.senderId === userId;
            
            return (
              <div
                key={message.id}
                className={`message-bubble ${isOwnMessage ? 'own' : 'other'}`}
              >
                {/* ✅ Foto de perfil */}
                {!isOwnMessage && (
                  <img
                    src={`/api/Users/${message.senderId}/profile-picture`}
                    alt={message.senderName}
                    className="message-avatar"
                    onError={(e) => {
                      (e.target as HTMLImageElement).src = '/default-avatar.png';
                    }}
                  />
                )}
                
                <div className="message-content">
                  {!isOwnMessage && (
                    <span className="message-sender">{message.senderName}</span>
                  )}
                  <p className="message-text">{message.content}</p>
                  <span className="message-time">
                    {new Date(message.sentAt).toLocaleTimeString('es-ES', {
                      hour: '2-digit',
                      minute: '2-digit'
                    })}
                  </span>
                  
                  {/* Adjuntos */}
                  {message.attachmentUrls.length > 0 && (
                    <div className="message-attachments">
                      {message.attachmentUrls.map((url, idx) => (
                        <img
                          key={idx}
                          src={url}
                          alt={`Adjunto ${idx + 1}`}
                          className="message-attachment"
                        />
                      ))}
                    </div>
                  )}
                </div>
              </div>
            );
          })
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="pre-hire-chat-input">
        <input
          type="text"
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyPress={handleKeyPress}
          placeholder="Escribe tu mensaje..."
          disabled={sendMessageMutation.isPending}
        />
        <button
          onClick={handleSend}
          disabled={!inputValue.trim() || sendMessageMutation.isPending}
          className="send-button"
        >
          {sendMessageMutation.isPending ? 'Enviando...' : 'Enviar'}
        </button>
      </div>
    </div>
  );
};
```

---

## 🔗 Integración en la Página de Detalles del Servicio

```typescript
// pages/ServiceDetailPage.tsx
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { PreHireChat } from '@/components/PreHireChat';
import { MessageCircle } from 'lucide-react';

export const ServiceDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const serviceId = parseInt(id || '0');
  const [showChat, setShowChat] = useState(false);
  const token = localStorage.getItem('token') || '';
  const userId = parseInt(localStorage.getItem('userId') || '0');

  // Obtener información del servicio
  const { data: service } = useQuery({
    queryKey: ['service', serviceId],
    queryFn: async () => {
      const response = await fetch(`/api/SearchService/${serviceId}`);
      return response.json();
    }
  });

  if (!service) {
    return <div>Cargando servicio...</div>;
  }

  return (
    <div className="service-detail-page">
      {/* Información del servicio */}
      <div className="service-info">
        <h1>{service.title || `Servicio #${service.id}`}</h1>
        <p className="price">{service.price}€</p>
        
        {/* Información del experto */}
        <div className="expert-info">
          <img
            src={service.expert?.profilePictureUrl || '/default-avatar.png'}
            alt={service.expert?.name}
            className="expert-avatar"
          />
          <div>
            <h2>{service.expert?.name}</h2>
            <p>{service.expert?.description}</p>
          </div>
        </div>
      </div>

      {/* Botón para abrir chat */}
      <div className="service-actions">
        <button
          onClick={() => setShowChat(!showChat)}
          className="chat-button"
        >
          <MessageCircle size={20} />
          {showChat ? 'Cerrar chat' : 'Chatear antes de contratar'}
        </button>
        
        <button className="hire-button">
          Contratar servicio
        </button>
      </div>

      {/* Chat pre-contratación */}
      {showChat && (
        <div className="pre-hire-chat-container">
          <PreHireChat
            serviceId={serviceId}
            token={token}
            userId={userId}
            onClose={() => setShowChat(false)}
          />
        </div>
      )}
    </div>
  );
};
```

---

## 🔄 Flujo Completo

### 1. Usuario ve el servicio
```
GET /api/SearchService/{id}
→ Muestra información del servicio
```

### 2. Usuario hace clic en "Chatear antes de contratar"
```
GET /api/Chat/conversation-by-service?searchServiceId={id}
→ Crea/obtiene conversación previa
→ Abre componente PreHireChat
```

### 3. Usuario envía mensajes
```
POST /api/Chat/message
→ Mensajes se guardan en conversación previa
→ Supabase Realtime notifica en tiempo real
→ Mensajes aparecen en ambos lados (cliente y experto)
```

### 4. Usuario contrata el servicio
```
POST /api/SearchHire
→ Backend busca conversación previa
→ Migra mensajes a conversación de SearchHire
→ Marca conversación previa como inactiva
```

### 5. Usuario ve chat del servicio contratado
```
GET /api/Chat/by-searchhire/{searchHireId}
→ Muestra TODOS los mensajes (previos + nuevos)
→ Los mensajes previos aparecen con su fecha original
```

---

## ⚠️ Diferencias con el Chat Normal

| Característica | Chat Pre-Contratación | Chat Normal (Post-Contratación) |
|---------------|----------------------|--------------------------------|
| **Endpoint** | `/api/Chat/conversation-by-service` | `/api/Chat/by-searchhire/{id}` |
| **Vinculación** | `SearchServiceId` | `SearchHireId` |
| **Funcionalidades** | Solo mensajes básicos | Mensajes + Deliverables + Disputas |
| **Foto de perfil** | ✅ Sí | ✅ Sí |
| **Tiempo real** | ✅ Sí (Supabase) | ✅ Sí (Supabase) |
| **Migración** | Se migra al contratar | Permanente |

---

## 🎨 Estilos CSS (Ejemplo)

```css
.pre-hire-chat {
  display: flex;
  flex-direction: column;
  height: 500px;
  border: 1px solid #e0e0e0;
  border-radius: 8px;
  background: white;
}

.pre-hire-chat-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid #e0e0e0;
  background: #f5f5f5;
}

.pre-hire-chat-header h3 {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
}

.pre-hire-chat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.message-bubble {
  display: flex;
  gap: 8px;
  max-width: 70%;
}

.message-bubble.own {
  align-self: flex-end;
  flex-direction: row-reverse;
}

.message-bubble.other {
  align-self: flex-start;
}

.message-avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  object-fit: cover;
}

.message-content {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.message-bubble.own .message-content {
  align-items: flex-end;
}

.message-bubble.other .message-content {
  align-items: flex-start;
}

.message-sender {
  font-size: 12px;
  font-weight: 600;
  color: #666;
}

.message-text {
  margin: 0;
  padding: 8px 12px;
  border-radius: 12px;
  background: #f0f0f0;
}

.message-bubble.own .message-text {
  background: #007bff;
  color: white;
}

.message-time {
  font-size: 10px;
  color: #999;
}

.pre-hire-chat-input {
  display: flex;
  gap: 8px;
  padding: 12px;
  border-top: 1px solid #e0e0e0;
}

.pre-hire-chat-input input {
  flex: 1;
  padding: 8px 12px;
  border: 1px solid #e0e0e0;
  border-radius: 20px;
  outline: none;
}

.pre-hire-chat-input input:focus {
  border-color: #007bff;
}

.send-button {
  padding: 8px 16px;
  background: #007bff;
  color: white;
  border: none;
  border-radius: 20px;
  cursor: pointer;
}

.send-button:disabled {
  background: #ccc;
  cursor: not-allowed;
}
```

---

## ✅ Checklist de Implementación

- [ ] Instalar dependencias: `@supabase/supabase-js`, `@tanstack/react-query`
- [ ] Configurar Supabase client en `lib/supabase.ts`
- [ ] Crear componente `PreHireChat`
- [ ] Integrar en página de detalles del servicio
- [ ] Agregar botón "Chatear antes de contratar"
- [ ] Probar envío de mensajes
- [ ] Probar tiempo real con Supabase
- [ ] Probar migración de mensajes al contratar
- [ ] Agregar estilos CSS
- [ ] Manejar errores y estados de carga

---

## 🔍 Debugging

### Verificar conexión a Supabase Realtime:

```typescript
// En el componente
useEffect(() => {
  const channel = supabase.channel('test-channel');
  channel.subscribe((status) => {
    console.log('Estado de Supabase:', status);
  });
}, []);
```

### Verificar mensajes en tiempo real:

```typescript
// Agregar logs en el handler de postgres_changes
.on('postgres_changes', {...}, (payload) => {
  console.log('📩 Nuevo mensaje recibido:', payload.new);
  // ... resto del código
})
```

---

## 📚 Referencias

- **Endpoint:** `GET /api/Chat/conversation-by-service?searchServiceId={id}`
- **Enviar mensaje:** `POST /api/Chat/message` (igual que el chat normal)
- **Supabase Realtime:** Usa los mismos canales que el chat normal
- **Guía backend:** Ver `GUIA_CHAT_PRE_CONTRATACION.md`

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Listo para implementar
