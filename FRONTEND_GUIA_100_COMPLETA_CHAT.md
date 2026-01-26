# 💬 Guía 100% Completa Frontend: Sistema de Chat Completo

## 📋 Índice

1. [Resumen Ejecutivo](#resumen-ejecutivo)
2. [Cambios en el Sistema](#cambios-en-el-sistema)
3. [Endpoints Completos](#endpoints-completos)
4. [DTOs y Estructuras de Datos](#dtos-y-estructuras-de-datos)
5. [Cómo Distinguir Tipos de Chat](#cómo-distinguir-tipos-de-chat)
6. [Flujos Completos](#flujos-completos)
7. [Implementación Completa](#implementación-completa)
8. [Supabase Realtime](#supabase-realtime)
9. [Migración de Mensajes](#migración-de-mensajes)
10. [Compatibilidad](#compatibilidad)

---

## 📊 Resumen Ejecutivo

### **¿Qué ha cambiado?**

1. ✅ **Nuevo:** Chat pre-contratación (antes de contratar un servicio)
2. ✅ **Cambio:** `ConversationDto.SearchHireId` ahora es **nullable**
3. ✅ **Nuevo:** `ConversationDto.SearchServiceId` (para conversaciones previas)
4. ✅ **Nuevo:** Endpoint para listar conversaciones previas del experto
5. ✅ **Cambio:** Endpoints existentes siguen funcionando igual (100% compatible)

### **Tipos de Chat**

| Tipo | Cuándo se usa | `SearchHireId` | `SearchServiceId` |
|------|---------------|----------------|-------------------|
| **Pre-contratación** | Antes de contratar | `null` | `int` (ID del servicio) |
| **Post-contratación** | Después de contratar | `int` (ID del SearchHire) | `null` |

---

## 🔄 Cambios en el Sistema

### **1. ConversationDto - Cambios Importantes**

#### **ANTES:**
```typescript
interface ConversationDto {
  id: number;
  searchHireId: number;  // ❌ Era obligatorio
  clientId: number;
  expertId: number;
  // ...
}
```

#### **AHORA:**
```typescript
interface ConversationDto {
  id: number;
  searchHireId: number | null;      // ✅ Ahora nullable
  searchServiceId: number | null;   // ✅ NUEVO
  clientId: number | null;          // ✅ Ya era nullable
  expertId: number | null;          // ✅ Ya era nullable
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  messages: MessageDto[];
}
```

**Cómo distinguir el tipo:**
```typescript
// Chat pre-contratación
if (conversation.searchHireId === null && conversation.searchServiceId !== null) {
  // Es chat pre-contratación
}

// Chat post-contratación
if (conversation.searchHireId !== null && conversation.searchServiceId === null) {
  // Es chat post-contratación
}
```

---

### **2. MessageDto - Sin Cambios (Ya era compatible)**

```typescript
interface MessageDto {
  id: number;
  conversationId: number;
  senderId: number | null;        // ✅ Ya era nullable
  content: string | null;         // ✅ Ya era nullable
  sentAt: string;
  isRead: boolean;
  senderName: string | null;      // ✅ Ya era nullable
  locationLatitude: string | null;
  locationLongitude: string | null;
  attachmentUrls: string[];
}
```

**Sin cambios necesarios** - Ya era compatible con usuarios eliminados.

---

## 🔌 Endpoints Completos

### **1. GET /api/Chat/conversation?searchId={id}**

**Descripción:** Obtiene conversación post-contratación por `searchId` (compatibilidad con sistema existente).

**Autenticación:** Requiere token JWT

**Parámetros:**
- `searchId` (query, requerido): ID del Search

**Comportamiento:**
- ✅ Solo busca conversaciones **post-contratación** (`SearchHireId != null`)
- ✅ Si no existe, crea una nueva conversación
- ✅ Funciona igual que antes (100% compatible)

**Respuesta (200):**
```json
{
  "id": 123,
  "searchHireId": 456,        // ✅ Siempre tiene valor (no null)
  "searchServiceId": null,     // ✅ Siempre null en este endpoint
  "clientId": 1,
  "expertId": 2,
  "isActive": true,
  "createdAt": "2026-01-26T19:00:00Z",
  "updatedAt": "2026-01-26T20:00:00Z",
  "messages": [...]
}
```

**Errores:**
- `401 Unauthorized`: Token inválido
- `404 Not Found`: Search hire no encontrado

---

### **2. GET /api/Chat/by-searchhire/{searchHireId}**

**Descripción:** Obtiene conversación post-contratación directamente por `SearchHireId`.

**Autenticación:** Requiere token JWT

**Parámetros:**
- `searchHireId` (path, requerido): ID del SearchHire

**Comportamiento:**
- ✅ Solo busca conversaciones **post-contratación** (`SearchHireId != null`)
- ✅ Si no existe, crea una nueva conversación
- ✅ Más eficiente que buscar por `searchId`

**Respuesta (200):** Igual que endpoint anterior

**Errores:**
- `401 Unauthorized`: Token inválido
- `404 Not Found`: Search hire no encontrado

---

### **3. GET /api/Chat/conversation-by-service?searchServiceId={id}** ⭐ NUEVO

**Descripción:** Obtiene o crea conversación **pre-contratación** por `SearchServiceId`.

**Autenticación:** Requiere token JWT

**Parámetros:**
- `searchServiceId` (query, requerido): ID del servicio

**Comportamiento según usuario:**

**Si el usuario es el CLIENTE:**
- ✅ Si existe conversación previa → La devuelve
- ✅ Si no existe → Crea una nueva conversación previa

**Si el usuario es el EXPERTO:**
- ✅ Si existe conversación previa → La devuelve (puede acceder y responder)
- ✅ Si no existe → Retorna 404 (solo el cliente puede iniciar)

**Respuesta (200):**
```json
{
  "id": 123,
  "searchHireId": null,        // ✅ null (aún no contratado)
  "searchServiceId": 456,      // ✅ ID del servicio
  "clientId": 1,
  "expertId": 2,
  "isActive": true,
  "createdAt": "2026-01-26T19:00:00Z",
  "updatedAt": "2026-01-26T20:00:00Z",
  "messages": [...]
}
```

**Errores:**
- `401 Unauthorized`: Token inválido
- `404 Not Found`: 
  - Servicio no encontrado
  - O conversación no encontrada (si es experto y no existe)

---

### **4. GET /api/Chat/pre-hire-conversations** ⭐ NUEVO

**Descripción:** Lista todas las conversaciones **pre-contratación** del experto.

**Autenticación:** Requiere token JWT

**Parámetros:** Ninguno (usa el userId del token)

**Comportamiento:**
- ✅ Solo muestra conversaciones donde el usuario es el experto
- ✅ Solo muestra conversaciones previas (`SearchHireId == null`)
- ✅ Ordenado por fecha de actualización (más recientes primero)

**Respuesta (200):**
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

**Errores:**
- `401 Unauthorized`: Token inválido

---

### **5. POST /api/Chat/message** (Sin cambios)

**Descripción:** Envía un mensaje en una conversación (funciona igual para pre y post-contratación).

**Autenticación:** Requiere token JWT

**Body (FormData):**
```
ConversationId: 123
Content: "Hola, ¿estás disponible?"
Attachments: [archivos opcionales]
LocationLatitude: [opcional]
LocationLongitude: [opcional]
```

**Comportamiento:**
- ✅ Funciona igual para conversaciones pre y post-contratación
- ✅ No hay cambios en este endpoint

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

### **6. POST /api/Chat/typing** (Sin cambios)

**Descripción:** Notifica que el usuario está escribiendo.

**Body (JSON):**
```json
{
  "conversationId": 123,
  "isTyping": true
}
```

---

### **7. GET /api/Chat/conversations** (Solo Admin, sin cambios)

**Descripción:** Obtiene todas las conversaciones (solo para admin).

**Sin cambios** - Funciona igual que antes.

---

## 📦 DTOs y Estructuras de Datos

### **ConversationDto Completo**

```typescript
interface ConversationDto {
  id: number;
  searchHireId: number | null;        // ✅ Nullable: null = pre-contratación
  searchServiceId: number | null;      // ✅ NUEVO: null = post-contratación
  clientId: number | null;            // ✅ Nullable (usuario eliminado)
  expertId: number | null;             // ✅ Nullable (usuario eliminado)
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
  messages: MessageDto[];
}

// ✅ Helper para distinguir tipo
function isPreHireChat(conversation: ConversationDto): boolean {
  return conversation.searchHireId === null && conversation.searchServiceId !== null;
}

function isPostHireChat(conversation: ConversationDto): boolean {
  return conversation.searchHireId !== null && conversation.searchServiceId === null;
}
```

---

### **PreHireConversationSummaryDto** ⭐ NUEVO

```typescript
interface PreHireConversationSummaryDto {
  conversationId: number;
  searchServiceId: number;
  serviceName: string;
  servicePrice: number;
  serviceImageUrl?: string;
  clientId?: number;
  clientName: string;
  clientProfilePictureUrl?: string;
  lastMessage?: MessageSummaryDto;
  unreadCount: number;
  createdAt: string;
  updatedAt: string;
}

interface MessageSummaryDto {
  id: number;
  content: string;
  sentAt: string;
  senderId?: number;
  senderName: string;
  isRead: boolean;
}
```

---

### **MessageDto** (Sin cambios)

```typescript
interface MessageDto {
  id: number;
  conversationId: number;
  senderId: number | null;           // ✅ Ya era nullable
  content: string | null;            // ✅ Ya era nullable
  sentAt: string;
  isRead: boolean;
  senderName: string | null;         // ✅ Ya era nullable
  locationLatitude: string | null;
  locationLongitude: string | null;
  attachmentUrls: string[];
}
```

---

## 🔍 Cómo Distinguir Tipos de Chat

### **Método 1: Verificar campos en ConversationDto**

```typescript
function getChatType(conversation: ConversationDto): 'pre-hire' | 'post-hire' | 'unknown' {
  if (conversation.searchHireId === null && conversation.searchServiceId !== null) {
    return 'pre-hire';
  }
  if (conversation.searchHireId !== null && conversation.searchServiceId === null) {
    return 'post-hire';
  }
  return 'unknown';
}
```

### **Método 2: Usar helper function**

```typescript
// ✅ Helper functions
export const ChatUtils = {
  isPreHireChat: (conversation: ConversationDto): boolean => {
    return conversation.searchHireId === null && 
           conversation.searchServiceId !== null;
  },
  
  isPostHireChat: (conversation: ConversationDto): boolean => {
    return conversation.searchHireId !== null && 
           conversation.searchServiceId === null;
  },
  
  getChatType: (conversation: ConversationDto): 'pre-hire' | 'post-hire' => {
    if (ChatUtils.isPreHireChat(conversation)) return 'pre-hire';
    return 'post-hire';
  }
};
```

### **Método 3: En el componente**

```typescript
const { data: conversation } = useQuery({
  queryKey: ['conversation', conversationId],
  queryFn: () => fetchConversation(conversationId)
});

const isPreHire = conversation?.searchHireId === null && 
                  conversation?.searchServiceId !== null;

// Mostrar UI diferente según el tipo
{isPreHire ? (
  <PreHireChatHeader serviceId={conversation.searchServiceId} />
) : (
  <PostHireChatHeader searchHireId={conversation.searchHireId} />
)}
```

---

## 🔄 Flujos Completos

### **Flujo 1: Cliente - Chat Pre-Contratación**

```
1. Cliente ve servicio
   → GET /api/SearchService/{id}
   → Muestra información del servicio

2. Cliente hace clic en "Chatear antes de contratar"
   → GET /api/Chat/conversation-by-service?searchServiceId={id}
   → Respuesta: ConversationDto { searchHireId: null, searchServiceId: 456 }
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
   → Conversación previa se actualiza (SearchHireId se asigna)

6. Cliente ve chat del servicio contratado
   → GET /api/Chat/by-searchhire/{searchHireId}
   → Respuesta: ConversationDto { searchHireId: 789, searchServiceId: null }
   → Muestra TODOS los mensajes (previos + nuevos)
```

---

### **Flujo 2: Experto - Ver y Responder Conversaciones Previas**

```
1. Experto entra a su panel
   → GET /api/Chat/pre-hire-conversations
   → Respuesta: List<PreHireConversationSummaryDto>
   → Ve lista de personas que le han escrito
   → Ve último mensaje y contador de no leídos

2. Experto hace clic en una conversación
   → GET /api/Chat/conversation-by-service?searchServiceId={id}
   → Respuesta: ConversationDto { searchHireId: null, searchServiceId: 456 }
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

### **Flujo 3: Chat Post-Contratación (Existente)**

```
1. Usuario accede a chat de servicio contratado
   → GET /api/Chat/conversation?searchId={id}
   → O: GET /api/Chat/by-searchhire/{searchHireId}
   → Respuesta: ConversationDto { searchHireId: 789, searchServiceId: null }
   → Abre componente PostHireChat

2. Usuario envía mensaje
   → POST /api/Chat/message
   → Funciona igual que antes

3. Mensajes en tiempo real
   → Supabase Realtime notifica cambios
   → Funciona igual que antes
```

---

## 💻 Implementación Completa

### **1. Configuración de Supabase**

```typescript
// lib/supabase.ts
import { createClient } from '@supabase/supabase-js'

const SUPABASE_URL = 'https://rveqsehzlvbttlpmsbmi.supabase.co'
const SUPABASE_ANON_KEY = 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'

export const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY)
```

---

### **2. Helpers para Distinguir Tipo de Chat**

```typescript
// utils/chatUtils.ts
import { ConversationDto } from '@/types/chat';

export const ChatUtils = {
  /**
   * Verifica si es chat pre-contratación
   */
  isPreHireChat: (conversation: ConversationDto): boolean => {
    return conversation.searchHireId === null && 
           conversation.searchServiceId !== null;
  },
  
  /**
   * Verifica si es chat post-contratación
   */
  isPostHireChat: (conversation: ConversationDto): boolean => {
    return conversation.searchHireId !== null && 
           conversation.searchServiceId === null;
  },
  
  /**
   * Obtiene el tipo de chat
   */
  getChatType: (conversation: ConversationDto): 'pre-hire' | 'post-hire' | 'unknown' => {
    if (ChatUtils.isPreHireChat(conversation)) return 'pre-hire';
    if (ChatUtils.isPostHireChat(conversation)) return 'post-hire';
    return 'unknown';
  },
  
  /**
   * Obtiene el ID para identificar el chat
   */
  getChatIdentifier: (conversation: ConversationDto): number | null => {
    if (ChatUtils.isPreHireChat(conversation)) {
      return conversation.searchServiceId;
    }
    if (ChatUtils.isPostHireChat(conversation)) {
      return conversation.searchHireId;
    }
    return null;
  }
};
```

---

### **3. Componente Unificado de Chat**

```typescript
// components/Chat.tsx
import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation } from '@tanstack/react-query';
import { supabase } from '@/lib/supabase';
import { ChatUtils } from '@/utils/chatUtils';
import { ConversationDto, MessageDto } from '@/types/chat';

interface ChatProps {
  conversationId?: number;
  searchServiceId?: number;    // Para chat pre-contratación
  searchHireId?: number;        // Para chat post-contratación
  searchId?: number;           // Para compatibilidad con sistema existente
  token: string;
  userId: number;
  onClose?: () => void;
}

export const Chat = ({ 
  conversationId,
  searchServiceId,
  searchHireId,
  searchId,
  token,
  userId,
  onClose
}: ChatProps) => {
  const [inputValue, setInputValue] = useState('');
  const [messages, setMessages] = useState<MessageDto[]>([]);
  const [isTyping, setIsTyping] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const API_URL = process.env.REACT_APP_API_URL || 'https://tu-api.com';

  // ✅ Determinar qué endpoint usar
  const getConversationEndpoint = () => {
    if (conversationId) {
      // Si ya tenemos el ID, podríamos usar otro endpoint
      // Por ahora usamos los endpoints existentes
    }
    if (searchServiceId) {
      return `/api/Chat/conversation-by-service?searchServiceId=${searchServiceId}`;
    }
    if (searchHireId) {
      return `/api/Chat/by-searchhire/${searchHireId}`;
    }
    if (searchId) {
      return `/api/Chat/conversation?searchId=${searchId}`;
    }
    return null;
  };

  // Obtener conversación
  const { data: conversation, isLoading, refetch } = useQuery<ConversationDto>({
    queryKey: ['conversation', conversationId, searchServiceId, searchHireId, searchId],
    queryFn: async () => {
      const endpoint = getConversationEndpoint();
      if (!endpoint) {
        throw new Error('No endpoint available');
      }

      const response = await fetch(`${API_URL}${endpoint}`, {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });

      if (!response.ok) {
        throw new Error('Error al obtener conversación');
      }

      return response.json();
    },
    enabled: !!(searchServiceId || searchHireId || searchId)
  });

  // Determinar tipo de chat
  const chatType = conversation ? ChatUtils.getChatType(conversation) : null;
  const isPreHire = chatType === 'pre-hire';
  const isPostHire = chatType === 'post-hire';

  // Inicializar mensajes
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
          const messageDto: MessageDto = {
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
            if (prev.some(m => m.id === messageDto.id)) {
              return prev; // Evitar duplicados
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
      .subscribe();

    return () => {
      supabase.removeChannel(channel);
    };
  }, [conversation?.id]);

  // Scroll automático
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
    }
  });

  const handleSend = () => {
    if (!inputValue.trim() || !conversation || sendMessageMutation.isPending) return;
    sendMessageMutation.mutate(inputValue.trim());
  };

  if (isLoading) {
    return <div>Cargando conversación...</div>;
  }

  if (!conversation) {
    return <div>No se pudo cargar la conversación</div>;
  }

  return (
    <div className="chat-container">
      {/* Header según tipo de chat */}
      <div className="chat-header">
        {isPreHire && (
          <h3>💬 Chat antes de contratar</h3>
        )}
        {isPostHire && (
          <h3>💬 Chat del servicio contratado</h3>
        )}
        {onClose && (
          <button onClick={onClose}>Cerrar</button>
        )}
      </div>

      {/* Lista de mensajes */}
      <div className="chat-messages">
        {messages.map((message) => {
          const isOwnMessage = message.senderId === userId;
          
          return (
            <div
              key={message.id}
              className={`message-bubble ${isOwnMessage ? 'own' : 'other'}`}
            >
              {!isOwnMessage && (
                <img
                  src={`${API_URL}/api/Users/${message.senderId}/profile-picture`}
                  alt={message.senderName || 'Usuario'}
                  className="message-avatar"
                />
              )}
              
              <div className="message-content">
                {!isOwnMessage && (
                  <span className="message-sender">
                    {message.senderName || '[Usuario eliminado]'}
                  </span>
                )}
                <p className="message-text">{message.content || '[Mensaje eliminado]'}</p>
                <span className="message-time">
                  {new Date(message.sentAt).toLocaleTimeString('es-ES', {
                    hour: '2-digit',
                    minute: '2-digit'
                  })}
                </span>
              </div>
            </div>
          );
        })}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="chat-input">
        <input
          type="text"
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyPress={(e) => e.key === 'Enter' && !e.shiftKey && handleSend()}
          placeholder="Escribe tu mensaje..."
          disabled={sendMessageMutation.isPending}
        />
        <button
          onClick={handleSend}
          disabled={!inputValue.trim() || sendMessageMutation.isPending}
        >
          {sendMessageMutation.isPending ? 'Enviando...' : 'Enviar'}
        </button>
      </div>
    </div>
  );
};
```

---

### **4. Componente: Lista de Conversaciones del Experto**

```typescript
// components/ExpertPreHireConversations.tsx
import { useQuery } from '@tanstack/react-query';
import { PreHireConversationSummaryDto } from '@/types/chat';

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

  const { data: conversations, isLoading } = useQuery<PreHireConversationSummaryDto[]>({
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
          onClick={() => onSelectConversation(conv.conversationId, conv.searchServiceId)}
        >
          <img
            src={conv.clientProfilePictureUrl || '/default-avatar.png'}
            alt={conv.clientName}
            className="client-avatar"
          />
          
          <div className="conversation-info">
            <h3>{conv.clientName}</h3>
            {conv.unreadCount > 0 && (
              <span className="unread-badge">{conv.unreadCount}</span>
            )}
            <p className="service-name">{conv.serviceName}</p>
            <p className="service-price">{conv.servicePrice}€</p>
            
            {conv.lastMessage && (
              <p className="last-message">
                {conv.lastMessage.senderName}: {conv.lastMessage.content}
              </p>
            )}
          </div>
        </div>
      ))}
    </div>
  );
};
```

---

### **5. Integración en Página de Detalles del Servicio**

```typescript
// pages/ServiceDetailPage.tsx
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { Chat } from '@/components/Chat';

export const ServiceDetailPage = () => {
  const { id } = useParams<{ id: string }>();
  const serviceId = parseInt(id || '0');
  const [showChat, setShowChat] = useState(false);
  const token = localStorage.getItem('token') || '';
  const userId = parseInt(localStorage.getItem('userId') || '0');

  return (
    <div className="service-detail-page">
      {/* Información del servicio */}
      <div className="service-info">
        {/* ... */}
      </div>

      {/* Botón para chatear */}
      <button
        onClick={() => setShowChat(!showChat)}
        className="chat-button"
      >
        {showChat ? 'Cerrar chat' : 'Chatear antes de contratar'}
      </button>

      {/* Chat pre-contratación */}
      {showChat && (
        <Chat
          searchServiceId={serviceId}
          token={token}
          userId={userId}
          onClose={() => setShowChat(false)}
        />
      )}
    </div>
  );
};
```

---

## 📡 Supabase Realtime

### **Configuración**

```typescript
// lib/supabase.ts
import { createClient } from '@supabase/supabase-js'

export const supabase = createClient(
  'https://rveqsehzlvbttlpmsbmi.supabase.co',
  'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'
)
```

### **Suscripción a Mensajes**

```typescript
// Funciona igual para pre y post-contratación
const channel = supabase
  .channel(`messages:conversation:${conversationId}`)
  .on(
    'postgres_changes',
    {
      event: 'INSERT',
      schema: 'public',
      table: 'Messages',
      filter: `ConversationId=eq.${conversationId}`
    },
    (payload) => {
      // Nuevo mensaje recibido
      const newMessage = payload.new;
      // Actualizar UI
    }
  )
  .on(
    'postgres_changes',
    {
      event: 'UPDATE',
      schema: 'public',
      table: 'Messages',
      filter: `ConversationId=eq.${conversationId}`
    },
    (payload) => {
      // Mensaje actualizado (ej: marcado como leído)
      const updatedMessage = payload.new;
      // Actualizar UI
    }
  )
  .subscribe();
```

---

## 🔄 Migración de Mensajes

### **¿Qué pasa cuando se contrata un servicio?**

1. **Backend automáticamente:**
   - Busca conversación previa con `SearchServiceId` y mismos `ClientId`/`ExpertId`
   - Si existe:
     - Asigna `SearchHireId` a la conversación previa
     - Limpia `SearchServiceId` (lo pone en `null`)
     - Los mensajes quedan en la misma conversación
   - Si no existe:
     - Crea nueva conversación con `SearchHireId`

2. **Frontend:**
   - No necesita hacer nada especial
   - Al obtener la conversación del `SearchHire`, verá todos los mensajes (previos + nuevos)

### **Ejemplo de Migración**

```typescript
// ANTES de contratar
{
  "id": 123,
  "searchHireId": null,
  "searchServiceId": 456,
  "messages": [
    { "id": 1, "content": "Hola" },
    { "id": 2, "content": "¿Disponible?" }
  ]
}

// DESPUÉS de contratar (automático)
{
  "id": 123,  // ✅ Mismo ID
  "searchHireId": 789,  // ✅ Ahora tiene SearchHireId
  "searchServiceId": null,  // ✅ Limpiado
  "messages": [
    { "id": 1, "content": "Hola" },      // ✅ Mensajes previos
    { "id": 2, "content": "¿Disponible?" }, // ✅ Mensajes previos
    { "id": 3, "content": "Sí, disponible" } // ✅ Nuevo mensaje
  ]
}
```

---

## ✅ Compatibilidad

### **Endpoints Existentes**

✅ **100% Compatibles** - No hay cambios en:
- `GET /api/Chat/conversation?searchId={id}`
- `GET /api/Chat/by-searchhire/{searchHireId}`
- `POST /api/Chat/message`
- `POST /api/Chat/typing`

### **DTOs Existentes**

✅ **Retrocompatibles:**
- `ConversationDto.SearchHireId` ahora es nullable, pero conversaciones existentes tienen valor
- `MessageDto` sin cambios

### **Frontend Existente**

✅ **No requiere cambios:**
- Si el frontend existente verifica `searchHireId !== null`, seguirá funcionando
- Si no verifica, también funcionará (nullable permite ambos casos)

---

## 📋 Checklist de Implementación

### **Configuración**
- [ ] Instalar `@supabase/supabase-js`
- [ ] Configurar cliente Supabase
- [ ] Configurar variables de entorno

### **Tipos TypeScript**
- [ ] Crear `ConversationDto` con `searchServiceId` nullable
- [ ] Crear `PreHireConversationSummaryDto`
- [ ] Crear `MessageSummaryDto`
- [ ] Crear helpers `ChatUtils`

### **Componentes Cliente**
- [ ] Crear componente `Chat` unificado
- [ ] Integrar en página de detalles del servicio
- [ ] Agregar botón "Chatear antes de contratar"
- [ ] Probar envío de mensajes
- [ ] Probar tiempo real

### **Componentes Experto**
- [ ] Crear componente `ExpertPreHireConversations`
- [ ] Crear panel del experto
- [ ] Integrar lista de conversaciones
- [ ] Integrar chat al seleccionar
- [ ] Probar respuesta a mensajes

### **Funcionalidades**
- [ ] Indicador de typing
- [ ] Contador de mensajes no leídos
- [ ] Scroll automático
- [ ] Manejo de errores
- [ ] Estados de carga
- [ ] Fotos de perfil
- [ ] Distinguir tipo de chat

### **Testing**
- [ ] Cliente puede iniciar conversación previa
- [ ] Experto puede ver conversaciones previas
- [ ] Mensajes en tiempo real funcionan
- [ ] Migración de mensajes al contratar
- [ ] Mensajes previos aparecen en chat contratado
- [ ] Endpoints existentes siguen funcionando

---

## 🎯 Resumen Final

### **Nuevos Endpoints:**
1. ✅ `GET /api/Chat/conversation-by-service?searchServiceId={id}` - Chat pre-contratación
2. ✅ `GET /api/Chat/pre-hire-conversations` - Lista del experto

### **Cambios en DTOs:**
1. ✅ `ConversationDto.SearchHireId` ahora nullable
2. ✅ `ConversationDto.SearchServiceId` nuevo campo

### **Compatibilidad:**
- ✅ 100% compatible con sistema existente
- ✅ No requiere cambios en código existente
- ✅ Retrocompatible

### **Funcionalidades:**
- ✅ Chat pre-contratación (cliente y experto)
- ✅ Chat post-contratación (sin cambios)
- ✅ Migración automática de mensajes
- ✅ Tiempo real con Supabase
- ✅ Lista de conversaciones del experto

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Listo para implementar completamente  
**Versión:** 1.0.0
