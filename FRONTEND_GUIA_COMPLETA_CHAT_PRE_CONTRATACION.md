# 💬 Guía Completa Frontend: Chat Pre-Contratación

## 📋 Resumen Ejecutivo

Se ha implementado una funcionalidad de **chat previo a contratar** que permite a clientes y expertos comunicarse **antes de que se contrate un servicio**. Los mensajes enviados en este chat se **migran automáticamente** al chat del servicio contratado cuando se realiza la contratación.

---

## 🎯 Funcionalidad Principal

### **Para el Cliente:**
- Puede chatear con el experto antes de contratar
- Ve la información del experto (foto, nombre)
- Los mensajes previos aparecen en el chat contratado después de contratar

### **Para el Experto:**
- Ve lista de personas que le han escrito antes de contratar
- Puede responder a los mensajes
- Puede ver información del cliente y del servicio
- Los mensajes previos aparecen en el chat contratado después de contratar

---

## 🔌 Endpoints Disponibles

### 1. **Listar Conversaciones Previas del Experto**

**Endpoint:** `GET /api/Chat/pre-hire-conversations`

**Descripción:** Lista todas las conversaciones previas a contratar donde el usuario es el experto.

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:** Ninguno (usa el userId del token)

**Respuesta (200):**
```json
[
  {
    "conversationId": 123,
    "searchServiceId": 456,
    "serviceName": "Reparación de ordenadores",
    "servicePrice": 50.00,
    "serviceImageUrl": "https://storage.googleapis.com/...",
    "clientId": 1,
    "clientName": "Juan Pérez",
    "clientProfilePictureUrl": "https://storage.googleapis.com/...",
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

**Errores:**
- `401 Unauthorized`: Token inválido o faltante

---

### 2. **Obtener/Crear Conversación Previa**

**Endpoint:** `GET /api/Chat/conversation-by-service?searchServiceId={id}`

**Descripción:** Obtiene o crea una conversación previa a contratar para un servicio específico.

**Autenticación:** Requiere token JWT (Bearer token)

**Parámetros:**
- `searchServiceId` (query, requerido): ID del servicio

**Comportamiento según usuario:**

**Si el usuario es el CLIENTE:**
- Si existe conversación previa → La devuelve
- Si no existe → Crea una nueva conversación previa

**Si el usuario es el EXPERTO:**
- Si existe conversación previa → La devuelve (puede acceder y responder)
- Si no existe → Retorna 404 (solo el cliente puede iniciar conversaciones)

**Respuesta (200):**
```json
{
  "id": 123,
  "searchHireId": null,           // ✅ null para conversaciones previas
  "searchServiceId": 456,         // ✅ ID del servicio
  "clientId": 1,
  "expertId": 2,
  "isActive": true,
  "createdAt": "2026-01-26T19:00:00Z",
  "updatedAt": "2026-01-26T20:00:00Z",
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

**Errores:**
- `401 Unauthorized`: Token inválido o faltante
- `404 Not Found`: Servicio no encontrado o conversación no encontrada (si es experto)

---

### 3. **Enviar Mensaje** (Igual que chat normal)

**Endpoint:** `POST /api/Chat/message`

**Descripción:** Envía un mensaje en una conversación (funciona igual para conversaciones previas y post-contratación).

**Autenticación:** Requiere token JWT (Bearer token)

**Body (FormData):**
```
ConversationId: 123
Content: "Hola, ¿estás disponible?"
Attachments: [archivos opcionales]
LocationLatitude: [opcional]
LocationLongitude: [opcional]
```

**Respuesta (200):**
```json
{
  "id": 789,
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
```

---

### 4. **Notificar Typing** (Opcional)

**Endpoint:** `POST /api/Chat/typing`

**Descripción:** Notifica que el usuario está escribiendo.

**Body (JSON):**
```json
{
  "conversationId": 123,
  "isTyping": true
}
```

---

## 🎨 Implementación Completa

### **1. Configuración de Supabase**

```typescript
// lib/supabase.ts
import { createClient } from '@supabase/supabase-js'

const SUPABASE_URL = 'https://rveqsehzlvbttlpmsbmi.supabase.co'
const SUPABASE_ANON_KEY = 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'

export const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY)
```

---

### **2. Componente: Lista de Conversaciones del Experto**

```typescript
// components/ExpertPreHireConversations.tsx
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { MessageCircle, Clock } from 'lucide-react';

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

interface ExpertPreHireConversationsProps {
  token: string;
  userId: number;
  onSelectConversation: (conversationId: number, searchServiceId: number) => void;
}

export const ExpertPreHireConversations = ({ 
  token, 
  userId,
  onSelectConversation 
}: ExpertPreHireConversationsProps) => {
  const API_URL = process.env.REACT_APP_API_URL || 'https://tu-api.com';

  const { data: conversations, isLoading, refetch } = useQuery<PreHireConversationSummary[]>({
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
    },
    refetchInterval: 30000 // Refrescar cada 30 segundos
  });

  if (isLoading) {
    return (
      <div className="loading-conversations">
        <p>Cargando conversaciones...</p>
      </div>
    );
  }

  if (!conversations || conversations.length === 0) {
    return (
      <div className="no-conversations">
        <MessageCircle size={48} className="empty-icon" />
        <p>No tienes conversaciones previas a contratar</p>
        <p className="subtitle">Los clientes que te escriban aparecerán aquí</p>
      </div>
    );
  }

  const formatTime = (dateString: string) => {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return 'Ahora';
    if (diffMins < 60) return `Hace ${diffMins} min`;
    if (diffHours < 24) return `Hace ${diffHours} h`;
    if (diffDays < 7) return `Hace ${diffDays} días`;
    return date.toLocaleDateString('es-ES');
  };

  return (
    <div className="expert-conversations-list">
      <div className="conversations-header">
        <h2>💬 Conversaciones antes de contratar</h2>
        <button onClick={() => refetch()} className="refresh-button">
          Actualizar
        </button>
      </div>

      <div className="conversations-grid">
        {conversations.map((conv) => (
          <div
            key={conv.conversationId}
            className={`conversation-card ${conv.unreadCount > 0 ? 'has-unread' : ''}`}
            onClick={() => onSelectConversation(conv.conversationId, conv.searchServiceId)}
          >
            {/* Header con foto del cliente y badge de no leídos */}
            <div className="conversation-header">
              <div className="client-info">
                <img
                  src={conv.clientProfilePictureUrl || '/default-avatar.png'}
                  alt={conv.clientName}
                  className="client-avatar"
                  onError={(e) => {
                    (e.target as HTMLImageElement).src = '/default-avatar.png';
                  }}
                />
                <div>
                  <h3 className="client-name">{conv.clientName}</h3>
                  <span className="service-name">{conv.serviceName}</span>
                </div>
              </div>
              
              {conv.unreadCount > 0 && (
                <span className="unread-badge">{conv.unreadCount}</span>
              )}
            </div>

            {/* Imagen del servicio */}
            {conv.serviceImageUrl && (
              <img
                src={conv.serviceImageUrl}
                alt={conv.serviceName}
                className="service-image"
              />
            )}

            {/* Último mensaje */}
            {conv.lastMessage && (
              <div className="last-message">
                <p className="message-preview">
                  <strong>{conv.lastMessage.senderName}:</strong> {conv.lastMessage.content}
                </p>
                <div className="message-meta">
                  <Clock size={14} />
                  <span>{formatTime(conv.lastMessage.sentAt)}</span>
                  {!conv.lastMessage.isRead && conv.lastMessage.senderId !== userId && (
                    <span className="unread-dot">●</span>
                  )}
                </div>
              </div>
            )}

            {/* Precio del servicio */}
            <div className="service-price">
              <strong>{conv.servicePrice}€</strong>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
};
```

---

### **3. Componente: Chat Pre-Contratación (Cliente y Experto)**

```typescript
// components/PreHireChat.tsx
import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { supabase } from '@/lib/supabase';
import { Send, X, Image as ImageIcon, MapPin } from 'lucide-react';

interface PreHireChatProps {
  serviceId: number;
  token: string;
  userId: number;
  onClose?: () => void;
  conversationId?: number; // Opcional: si ya se conoce el ID
}

interface Message {
  id: number;
  conversationId: number;
  senderId: number | null;
  content: string;
  sentAt: string;
  isRead: boolean;
  senderName: string;
  locationLatitude?: string | null;
  locationLongitude?: string | null;
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

export const PreHireChat = ({ 
  serviceId, 
  token, 
  userId, 
  onClose,
  conversationId 
}: PreHireChatProps) => {
  const [inputValue, setInputValue] = useState('');
  const [isTyping, setIsTyping] = useState(false);
  const [typingUsers, setTypingUsers] = useState<number[]>([]);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const typingTimeoutRef = useRef<NodeJS.Timeout>();
  const API_URL = process.env.REACT_APP_API_URL || 'https://tu-api.com';

  // Obtener o crear conversación previa
  const { data: conversation, isLoading, refetch } = useQuery<Conversation>({
    queryKey: ['pre-hire-conversation', serviceId, conversationId],
    queryFn: async () => {
      // Si ya tenemos el conversationId, podríamos usar otro endpoint
      // Por ahora usamos el endpoint por serviceId
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
        if (response.status === 404) {
          throw new Error('No se encontró la conversación. Solo el cliente puede iniciar conversaciones.');
        }
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
      setMessages(conversation.messages.sort((a, b) => 
        new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
      ));
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
          const messageDto: Message = {
            id: newMessage.Id,
            conversationId: newMessage.ConversationId,
            senderId: newMessage.SenderId,
            content: newMessage.Content || '',
            sentAt: newMessage.SentAt,
            isRead: newMessage.IsRead,
            senderName: '', // Se obtendrá del sender
            locationLatitude: newMessage.LocationLatitude,
            locationLongitude: newMessage.LocationLongitude,
            attachmentUrls: []
          };
          
          setMessages(prev => {
            // Evitar duplicados
            if (prev.some(m => m.id === messageDto.id)) {
              return prev;
            }
            return [...prev, messageDto].sort((a, b) => 
              new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
            );
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
      .on(
        'broadcast',
        { event: 'typing' },
        ({ payload }) => {
          if (payload.userId !== userId && payload.isTyping) {
            setTypingUsers(prev => [...prev.filter(id => id !== payload.userId), payload.userId]);
          } else if (!payload.isTyping) {
            setTypingUsers(prev => prev.filter(id => id !== payload.userId));
          }
        }
      )
      .subscribe((status) => {
        console.log(`📡 Estado de suscripción: ${status}`);
      });

    return () => {
      supabase.removeChannel(channel);
    };
  }, [conversation?.id, userId]);

  // Scroll automático al final
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // Enviar mensaje
  const sendMessageMutation = useMutation({
    mutationFn: async (content: string) => {
      if (!conversation) throw new Error('No hay conversación');

      const formData = new FormData();
      formData.append('ConversationId', conversation.id.toString());
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
      // Notificar que dejó de escribir
      if (conversation) {
        notifyTyping(conversation.id, false);
      }
    }
  });

  const handleSend = () => {
    if (!inputValue.trim() || !conversation || sendMessageMutation.isPending) return;
    sendMessageMutation.mutate(inputValue.trim());
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  // Notificar typing
  const notifyTyping = async (convId: number, typing: boolean) => {
    try {
      await fetch(`${API_URL}/api/Chat/typing`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({
          conversationId: convId,
          isTyping: typing
        })
      });
    } catch (error) {
      console.error('Error notificando typing:', error);
    }
  };

  // Indicador de typing
  useEffect(() => {
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current);
    }

    if (inputValue.length > 0 && !isTyping && conversation) {
      setIsTyping(true);
      notifyTyping(conversation.id, true);
    }

    typingTimeoutRef.current = setTimeout(() => {
      if (isTyping && conversation) {
        setIsTyping(false);
        notifyTyping(conversation.id, false);
      }
    }, 2000);

    return () => {
      if (typingTimeoutRef.current) {
        clearTimeout(typingTimeoutRef.current);
      }
    };
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
        {onClose && (
          <button onClick={onClose}>Cerrar</button>
        )}
      </div>
    );
  }

  const otherUserId = conversation.clientId === userId 
    ? conversation.expertId 
    : conversation.clientId;

  const otherUserName = conversation.clientId === userId
    ? conversation.expertId ? 'Experto' : 'Usuario'
    : conversation.client?.Name || 'Cliente';

  return (
    <div className="pre-hire-chat">
      {/* Header */}
      <div className="pre-hire-chat-header">
        <div className="header-info">
          <h3>💬 Chat antes de contratar</h3>
          <p className="chat-subtitle">Conversación sobre el servicio</p>
        </div>
        {onClose && (
          <button onClick={onClose} className="close-button" aria-label="Cerrar chat">
            <X size={20} />
          </button>
        )}
      </div>

      {/* Lista de mensajes */}
      <div className="pre-hire-chat-messages">
        {messages.length === 0 ? (
          <div className="no-messages">
            <MessageCircle size={48} className="empty-icon" />
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
                    src={`${API_URL}/api/Users/${message.senderId}/profile-picture`}
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

                  {/* Ubicación */}
                  {message.locationLatitude && message.locationLongitude && (
                    <a
                      href={`https://www.google.com/maps?q=${message.locationLatitude},${message.locationLongitude}`}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="message-location"
                    >
                      <MapPin size={14} />
                      Ver ubicación
                    </a>
                  )}
                  
                  <span className="message-time">
                    {new Date(message.sentAt).toLocaleTimeString('es-ES', {
                      hour: '2-digit',
                      minute: '2-digit'
                    })}
                  </span>
                </div>
              </div>
            );
          })
        )}

        {/* Indicador de typing */}
        {typingUsers.length > 0 && (
          <div className="typing-indicator">
            <span>{otherUserName} está escribiendo...</span>
          </div>
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
          className="chat-input"
        />
        <button
          onClick={handleSend}
          disabled={!inputValue.trim() || sendMessageMutation.isPending}
          className="send-button"
          aria-label="Enviar mensaje"
        >
          {sendMessageMutation.isPending ? (
            <span>Enviando...</span>
          ) : (
            <Send size={20} />
          )}
        </button>
      </div>
    </div>
  );
};
```

---

### **4. Integración en Página de Detalles del Servicio (Cliente)**

```typescript
// pages/ServiceDetailPage.tsx
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { PreHireChat } from '@/components/PreHireChat';
import { MessageCircle, Calendar } from 'lucide-react';

export const ServiceDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const serviceId = parseInt(id || '0');
  const [showChat, setShowChat] = useState(false);
  const token = localStorage.getItem('token') || '';
  const userId = parseInt(localStorage.getItem('userId') || '0');

  // Obtener información del servicio
  const { data: service, isLoading } = useQuery({
    queryKey: ['service', serviceId],
    queryFn: async () => {
      const response = await fetch(`/api/SearchService/${serviceId}`, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });
      if (!response.ok) throw new Error('Error al cargar servicio');
      return response.json();
    },
    enabled: !!serviceId && !!token
  });

  if (isLoading) {
    return <div>Cargando servicio...</div>;
  }

  if (!service) {
    return <div>Servicio no encontrado</div>;
  }

  // Verificar si el usuario es el experto
  const isExpert = service.expertProfile?.userId === userId;

  return (
    <div className="service-detail-page">
      {/* Información del servicio */}
      <div className="service-info">
        <div className="service-header">
          <h1>{service.title || `Servicio #${service.id}`}</h1>
          <p className="price">{service.price}€</p>
        </div>
        
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

        {/* Imágenes del servicio */}
        {service.images && service.images.length > 0 && (
          <div className="service-images">
            {service.images.map((img: any) => (
              <img key={img.id} src={img.url} alt="Servicio" />
            ))}
          </div>
        )}
      </div>

      {/* Acciones */}
      {!isExpert && (
        <div className="service-actions">
          <button
            onClick={() => setShowChat(!showChat)}
            className={`chat-button ${showChat ? 'active' : ''}`}
          >
            <MessageCircle size={20} />
            {showChat ? 'Cerrar chat' : 'Chatear antes de contratar'}
          </button>
          
          <button className="hire-button">
            <Calendar size={20} />
            Contratar servicio
          </button>
        </div>
      )}

      {/* Chat pre-contratación */}
      {showChat && !isExpert && (
        <div className="pre-hire-chat-container">
          <PreHireChat
            serviceId={serviceId}
            token={token}
            userId={userId}
            onClose={() => setShowChat(false)}
          />
        </div>
      )}

      {/* Mensaje para experto */}
      {isExpert && (
        <div className="expert-message">
          <p>Este es tu servicio. Los clientes pueden chatear contigo antes de contratar.</p>
          <p>Ve a tu panel de experto para ver las conversaciones.</p>
        </div>
      )}
    </div>
  );
};
```

---

### **5. Panel del Experto: Lista de Conversaciones**

```typescript
// pages/ExpertDashboard.tsx
import { useState } from 'react';
import { ExpertPreHireConversations } from '@/components/ExpertPreHireConversations';
import { PreHireChat } from '@/components/PreHireChat';

export const ExpertDashboard = () => {
  const [selectedConversation, setSelectedConversation] = useState<{
    conversationId: number;
    searchServiceId: number;
  } | null>(null);
  
  const token = localStorage.getItem('token') || '';
  const userId = parseInt(localStorage.getItem('userId') || '0');

  const handleSelectConversation = (conversationId: number, searchServiceId: number) => {
    setSelectedConversation({ conversationId, searchServiceId });
  };

  return (
    <div className="expert-dashboard">
      <h1>Panel del Experto</h1>
      
      <div className="dashboard-content">
        {/* Lista de conversaciones */}
        <div className="conversations-sidebar">
          <ExpertPreHireConversations
            token={token}
            userId={userId}
            onSelectConversation={handleSelectConversation}
          />
        </div>

        {/* Chat seleccionado */}
        {selectedConversation && (
          <div className="chat-panel">
            <PreHireChat
              serviceId={selectedConversation.searchServiceId}
              token={token}
              userId={userId}
              conversationId={selectedConversation.conversationId}
              onClose={() => setSelectedConversation(null)}
            />
          </div>
        )}

        {/* Mensaje cuando no hay conversación seleccionada */}
        {!selectedConversation && (
          <div className="no-selection">
            <p>Selecciona una conversación para ver los mensajes</p>
          </div>
        )}
      </div>
    </div>
  );
};
```

---

## 🔄 Flujos Completos

### **Flujo Cliente: Chatear Antes de Contratar**

```
1. Cliente ve servicio
   → GET /api/SearchService/{id}
   → Muestra información del servicio

2. Cliente hace clic en "Chatear antes de contratar"
   → GET /api/Chat/conversation-by-service?searchServiceId={id}
   → Crea/obtiene conversación previa
   → Abre componente PreHireChat

3. Cliente envía mensaje
   → POST /api/Chat/message
   → Mensaje se guarda en BD
   → Supabase Realtime notifica al experto

4. Experto responde
   → Supabase Realtime notifica al cliente
   → Mensaje aparece en tiempo real

5. Cliente contrata el servicio
   → POST /api/SearchHire
   → Backend migra mensajes automáticamente
   → Conversación previa se marca como inactiva

6. Cliente ve chat del servicio contratado
   → GET /api/Chat/by-searchhire/{searchHireId}
   → Muestra TODOS los mensajes (previos + nuevos)
```

---

### **Flujo Experto: Ver y Responder Conversaciones**

```
1. Experto entra a su panel
   → GET /api/Chat/pre-hire-conversations
   → Ve lista de personas que le han escrito
   → Ve último mensaje y contador de no leídos

2. Experto hace clic en una conversación
   → GET /api/Chat/conversation-by-service?searchServiceId={id}
   → Obtiene conversación completa
   → Abre componente PreHireChat

3. Experto responde
   → POST /api/Chat/message
   → Mensaje se guarda en BD
   → Supabase Realtime notifica al cliente

4. Cliente contrata el servicio
   → POST /api/SearchHire
   → Backend migra mensajes automáticamente
   → Experto puede seguir chateando en chat contratado
```

---

## 🎨 Estilos CSS Completos

```css
/* PreHireChat Component */
.pre-hire-chat {
  display: flex;
  flex-direction: column;
  height: 600px;
  max-height: 80vh;
  border: 1px solid #e0e0e0;
  border-radius: 12px;
  background: white;
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.1);
  overflow: hidden;
}

.pre-hire-chat-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 16px 20px;
  border-bottom: 1px solid #e0e0e0;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
}

.pre-hire-chat-header h3 {
  margin: 0;
  font-size: 18px;
  font-weight: 600;
}

.chat-subtitle {
  margin: 4px 0 0 0;
  font-size: 12px;
  opacity: 0.9;
}

.close-button {
  background: rgba(255, 255, 255, 0.2);
  border: none;
  border-radius: 50%;
  width: 32px;
  height: 32px;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  color: white;
  transition: background 0.2s;
}

.close-button:hover {
  background: rgba(255, 255, 255, 0.3);
}

.pre-hire-chat-messages {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 16px;
  background: #f8f9fa;
}

.no-messages {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: 100%;
  color: #999;
}

.empty-icon {
  opacity: 0.3;
  margin-bottom: 16px;
}

.message-bubble {
  display: flex;
  gap: 12px;
  max-width: 75%;
  animation: slideIn 0.3s ease-out;
}

@keyframes slideIn {
  from {
    opacity: 0;
    transform: translateY(10px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.message-bubble.own {
  align-self: flex-end;
  flex-direction: row-reverse;
}

.message-bubble.other {
  align-self: flex-start;
}

.message-avatar {
  width: 40px;
  height: 40px;
  border-radius: 50%;
  object-fit: cover;
  flex-shrink: 0;
  border: 2px solid #e0e0e0;
}

.message-content {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
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
  margin-bottom: 4px;
}

.message-text {
  margin: 0;
  padding: 10px 14px;
  border-radius: 16px;
  word-wrap: break-word;
  background: #e9ecef;
  color: #333;
  line-height: 1.4;
}

.message-bubble.own .message-text {
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
}

.message-time {
  font-size: 11px;
  color: #999;
  margin-top: 4px;
}

.message-attachments {
  display: flex;
  gap: 8px;
  margin-top: 8px;
  flex-wrap: wrap;
}

.message-attachment {
  max-width: 200px;
  max-height: 200px;
  border-radius: 8px;
  object-fit: cover;
}

.message-location {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  color: #667eea;
  text-decoration: none;
  font-size: 12px;
  margin-top: 4px;
}

.message-location:hover {
  text-decoration: underline;
}

.typing-indicator {
  padding: 8px 16px;
  color: #999;
  font-size: 12px;
  font-style: italic;
}

.pre-hire-chat-input {
  display: flex;
  gap: 8px;
  padding: 16px;
  border-top: 1px solid #e0e0e0;
  background: white;
}

.chat-input {
  flex: 1;
  padding: 12px 16px;
  border: 2px solid #e0e0e0;
  border-radius: 24px;
  outline: none;
  font-size: 14px;
  transition: border-color 0.2s;
}

.chat-input:focus {
  border-color: #667eea;
}

.chat-input:disabled {
  background: #f5f5f5;
  cursor: not-allowed;
}

.send-button {
  padding: 12px 20px;
  background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
  color: white;
  border: none;
  border-radius: 24px;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: transform 0.2s, opacity 0.2s;
  min-width: 48px;
}

.send-button:hover:not(:disabled) {
  transform: scale(1.05);
}

.send-button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  transform: none;
}

/* Expert Conversations List */
.expert-conversations-list {
  padding: 20px;
}

.conversations-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 20px;
}

.conversations-header h2 {
  margin: 0;
  font-size: 24px;
  font-weight: 600;
}

.refresh-button {
  padding: 8px 16px;
  background: #667eea;
  color: white;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  font-size: 14px;
}

.conversations-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 16px;
}

.conversation-card {
  background: white;
  border: 2px solid #e0e0e0;
  border-radius: 12px;
  padding: 16px;
  cursor: pointer;
  transition: all 0.2s;
}

.conversation-card:hover {
  border-color: #667eea;
  box-shadow: 0 4px 12px rgba(102, 126, 234, 0.15);
  transform: translateY(-2px);
}

.conversation-card.has-unread {
  border-color: #667eea;
  background: #f8f9ff;
}

.conversation-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 12px;
}

.client-info {
  display: flex;
  gap: 12px;
  align-items: center;
  flex: 1;
}

.client-avatar {
  width: 48px;
  height: 48px;
  border-radius: 50%;
  object-fit: cover;
  border: 2px solid #e0e0e0;
}

.client-name {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  color: #333;
}

.service-name {
  font-size: 12px;
  color: #666;
  margin-top: 2px;
}

.unread-badge {
  background: #667eea;
  color: white;
  border-radius: 12px;
  padding: 4px 8px;
  font-size: 12px;
  font-weight: 600;
  min-width: 24px;
  text-align: center;
}

.service-image {
  width: 100%;
  height: 120px;
  object-fit: cover;
  border-radius: 8px;
  margin-bottom: 12px;
}

.last-message {
  margin: 12px 0;
}

.message-preview {
  margin: 0 0 4px 0;
  font-size: 14px;
  color: #666;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.message-preview strong {
  color: #333;
}

.message-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 11px;
  color: #999;
}

.unread-dot {
  color: #667eea;
  font-size: 8px;
}

.service-price {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid #e0e0e0;
  font-size: 18px;
  font-weight: 600;
  color: #667eea;
}

.no-conversations {
  text-align: center;
  padding: 60px 20px;
  color: #999;
}

.no-conversations .empty-icon {
  opacity: 0.3;
  margin-bottom: 16px;
}

.subtitle {
  font-size: 14px;
  margin-top: 8px;
  opacity: 0.7;
}

/* Responsive */
@media (max-width: 768px) {
  .pre-hire-chat {
    height: 100vh;
    max-height: 100vh;
    border-radius: 0;
  }

  .conversations-grid {
    grid-template-columns: 1fr;
  }
}
```

---

## ✅ Checklist de Implementación

### **Configuración Inicial**
- [ ] Instalar `@supabase/supabase-js`
- [ ] Configurar cliente Supabase en `lib/supabase.ts`
- [ ] Configurar variables de entorno (`REACT_APP_API_URL`)

### **Componentes Cliente**
- [ ] Crear componente `PreHireChat`
- [ ] Integrar en página de detalles del servicio
- [ ] Agregar botón "Chatear antes de contratar"
- [ ] Probar envío de mensajes
- [ ] Probar tiempo real con Supabase

### **Componentes Experto**
- [ ] Crear componente `ExpertPreHireConversations`
- [ ] Crear panel del experto
- [ ] Integrar lista de conversaciones
- [ ] Integrar chat al seleccionar conversación
- [ ] Probar respuesta a mensajes

### **Funcionalidades**
- [ ] Indicador de typing
- [ ] Contador de mensajes no leídos
- [ ] Scroll automático
- [ ] Manejo de errores
- [ ] Estados de carga
- [ ] Fotos de perfil
- [ ] Adjuntos (opcional)

### **Testing**
- [ ] Cliente puede iniciar conversación
- [ ] Experto puede ver conversaciones
- [ ] Mensajes en tiempo real funcionan
- [ ] Migración de mensajes al contratar
- [ ] Mensajes previos aparecen en chat contratado

---

## 🔍 Debugging y Troubleshooting

### **Problema: No se reciben mensajes en tiempo real**

```typescript
// Verificar conexión a Supabase
useEffect(() => {
  const channel = supabase.channel('test');
  channel.subscribe((status) => {
    console.log('Estado Supabase:', status);
    // Debe ser 'SUBSCRIBED'
  });
}, []);
```

### **Problema: Mensajes duplicados**

```typescript
// Agregar verificación de duplicados
setMessages(prev => {
  if (prev.some(m => m.id === messageDto.id)) {
    return prev; // Ya existe, no agregar
  }
  return [...prev, messageDto];
});
```

### **Problema: Conversación no se crea**

```typescript
// Verificar que el usuario no sea el experto
// Solo el cliente puede crear conversaciones previas
// El experto solo puede acceder a existentes
```

---

## 📚 Referencias

- **Endpoint lista experto:** `GET /api/Chat/pre-hire-conversations`
- **Endpoint conversación:** `GET /api/Chat/conversation-by-service?searchServiceId={id}`
- **Enviar mensaje:** `POST /api/Chat/message` (igual que chat normal)
- **Supabase Realtime:** Usa `postgres_changes` en tabla `Messages`
- **Guía backend:** Ver `GUIA_CHAT_PRE_CONTRATACION.md`

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Listo para implementar completamente
