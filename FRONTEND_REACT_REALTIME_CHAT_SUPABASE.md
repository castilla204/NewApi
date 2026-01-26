# 💬 Guía Frontend React: Chat en Tiempo Real con Supabase

## 📋 Resumen

Esta guía explica cómo implementar el chat en tiempo real usando **Supabase Realtime** para que los mensajes aparezcan **instantáneamente** sin necesidad de recargar la página.

---

## 🎯 Objetivo

- ✅ Los mensajes enviados aparecen al momento
- ✅ Los mensajes recibidos aparecen al momento
- ✅ Sin necesidad de recargar la página
- ✅ Actualizaciones en tiempo real (marcar como leído, etc.)

---

## 🔧 Configuración Inicial

### **1. Instalar Supabase**

```bash
npm install @supabase/supabase-js
# o
yarn add @supabase/supabase-js
```

### **2. Configurar Cliente Supabase**

```typescript
// lib/supabase.ts
import { createClient } from '@supabase/supabase-js'

const SUPABASE_URL = 'https://rveqsehzlvbttlpmsbmi.supabase.co'
const SUPABASE_ANON_KEY = 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'

export const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY)
```

---

## 💻 Implementación Completa

### **Componente de Chat con Tiempo Real**

```typescript
// components/Chat.tsx
import { useState, useEffect, useRef } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { supabase } from '@/lib/supabase';

interface Message {
  id: number;
  conversationId: number;
  senderId: number | null;
  content: string | null;
  sentAt: string;
  isRead: boolean;
  senderName: string | null;
  locationLatitude: string | null;
  locationLongitude: string | null;
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

interface ChatProps {
  conversationId: number;
  token: string;
  userId: number;
  apiUrl: string;
}

export const Chat = ({ conversationId, token, userId, apiUrl }: ChatProps) => {
  const [inputValue, setInputValue] = useState('');
  const [messages, setMessages] = useState<Message[]>([]);
  const [isConnected, setIsConnected] = useState(false);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const channelRef = useRef<any>(null);
  const queryClient = useQueryClient();

  // ✅ 1. Cargar conversación inicial
  const { data: conversation, isLoading } = useQuery<Conversation>({
    queryKey: ['conversation', conversationId],
    queryFn: async () => {
      const response = await fetch(
        `${apiUrl}/api/Chat/by-searchhire/${conversationId}`,
        // O usar: `${apiUrl}/api/Chat/conversation-by-service?searchServiceId=${serviceId}`
        {
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          }
        }
      );

      if (!response.ok) {
        throw new Error('Error al cargar conversación');
      }

      return response.json();
    },
    enabled: !!conversationId && !!token
  });

  // ✅ 2. Inicializar mensajes desde la conversación
  useEffect(() => {
    if (conversation?.messages) {
      const sortedMessages = conversation.messages.sort((a, b) => 
        new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
      );
      setMessages(sortedMessages);
    }
  }, [conversation]);

  // ✅ 3. SUSCRIBIRSE A CAMBIOS EN TIEMPO REAL
  useEffect(() => {
    if (!conversationId) return;

    console.log('🔌 Conectando a Supabase Realtime para conversación:', conversationId);

    // Crear canal único para esta conversación
    const channelName = `messages:conversation:${conversationId}`;
    const channel = supabase
      .channel(channelName)
      
      // ✅ SUSCRIPCIÓN 1: Nuevos mensajes (INSERT)
      .on(
        'postgres_changes',
        {
          event: 'INSERT',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        async (payload) => {
          console.log('📨 Nuevo mensaje recibido:', payload);
          
          const newMessage = payload.new as any;
          
          // ✅ Obtener información del sender (nombre, etc.)
          let senderName = '[Usuario eliminado]';
          if (newMessage.SenderId) {
            try {
              const senderResponse = await fetch(
                `${apiUrl}/api/Users/${newMessage.SenderId}/profile-picture`,
                {
                  headers: { 'Authorization': `Bearer ${token}` }
                }
              );
              // Obtener nombre del sender desde la conversación o hacer otra llamada
              // Por ahora usamos el nombre que viene en el payload si está disponible
            } catch (error) {
              console.error('Error obteniendo info del sender:', error);
            }
          }

          // ✅ Crear objeto MessageDto
          const messageDto: Message = {
            id: newMessage.Id,
            conversationId: newMessage.ConversationId,
            senderId: newMessage.SenderId,
            content: newMessage.Content || '',
            sentAt: newMessage.SentAt,
            isRead: newMessage.IsRead || false,
            senderName: senderName,
            locationLatitude: newMessage.LocationLatitude,
            locationLongitude: newMessage.LocationLongitude,
            attachmentUrls: [] // Se cargarán después si es necesario
          };

          // ✅ Agregar mensaje al estado (evitar duplicados)
          setMessages(prev => {
            // Verificar si el mensaje ya existe (evitar duplicados)
            if (prev.some(m => m.id === messageDto.id)) {
              console.log('⚠️ Mensaje duplicado ignorado:', messageDto.id);
              return prev;
            }

            // Agregar nuevo mensaje y ordenar por fecha
            const updated = [...prev, messageDto].sort((a, b) => 
              new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
            );

            console.log('✅ Mensaje agregado. Total mensajes:', updated.length);
            return updated;
          });

          // ✅ Invalidar query para refrescar datos si es necesario
          queryClient.invalidateQueries({ queryKey: ['conversation', conversationId] });
        }
      )

      // ✅ SUSCRIPCIÓN 2: Mensajes actualizados (UPDATE)
      .on(
        'postgres_changes',
        {
          event: 'UPDATE',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        (payload) => {
          console.log('🔄 Mensaje actualizado:', payload);
          
          const updatedMessage = payload.new as any;
          
          // ✅ Actualizar mensaje en el estado
          setMessages(prev =>
            prev.map(msg =>
              msg.id === updatedMessage.Id
                ? {
                    ...msg,
                    isRead: updatedMessage.IsRead,
                    content: updatedMessage.Content || msg.content,
                    // Actualizar otros campos si es necesario
                  }
                : msg
            )
          );
        }
      )

      // ✅ SUSCRIPCIÓN 3: Mensajes eliminados (DELETE) - Opcional
      .on(
        'postgres_changes',
        {
          event: 'DELETE',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        (payload) => {
          console.log('🗑️ Mensaje eliminado:', payload);
          
          const deletedMessage = payload.old as any;
          
          // ✅ Remover mensaje del estado
          setMessages(prev => prev.filter(msg => msg.id !== deletedMessage.Id));
        }
      )

      // ✅ SUSCRIPCIÓN 4: Broadcast para typing indicators (Opcional)
      .on(
        'broadcast',
        { event: 'typing' },
        ({ payload }) => {
          console.log('⌨️ Typing indicator:', payload);
          // Manejar indicador de typing aquí
          // Ejemplo: mostrar "Usuario está escribiendo..."
        }
      )

      // ✅ Suscribirse al canal
      .subscribe((status) => {
        console.log(`📡 Estado de suscripción: ${status}`);
        setIsConnected(status === 'SUBSCRIBED');
        
        if (status === 'SUBSCRIBED') {
          console.log('✅ Conectado a Supabase Realtime');
        } else if (status === 'CHANNEL_ERROR') {
          console.error('❌ Error en el canal de Supabase');
        }
      });

    // Guardar referencia del canal para limpiar después
    channelRef.current = channel;

    // ✅ Limpiar suscripción al desmontar el componente
    return () => {
      console.log('🔌 Desconectando de Supabase Realtime');
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current);
      }
    };
  }, [conversationId, token, apiUrl, queryClient]);

  // ✅ 4. Scroll automático al final cuando hay nuevos mensajes
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages]);

  // ✅ 5. Enviar mensaje
  const sendMessageMutation = useMutation({
    mutationFn: async (content: string) => {
      if (!conversation) throw new Error('No hay conversación');

      const formData = new FormData();
      formData.append('ConversationId', conversation.id.toString());
      formData.append('Content', content);

      const response = await fetch(`${apiUrl}/api/Chat/message`, {
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
    onSuccess: (data) => {
      console.log('✅ Mensaje enviado:', data);
      setInputValue('');
      
      // ✅ El mensaje aparecerá automáticamente vía Supabase Realtime
      // No necesitamos agregarlo manualmente al estado
    },
    onError: (error) => {
      console.error('❌ Error al enviar mensaje:', error);
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

  if (isLoading) {
    return (
      <div className="chat-loading">
        <p>Cargando conversación...</p>
      </div>
    );
  }

  if (!conversation) {
    return (
      <div className="chat-error">
        <p>No se pudo cargar la conversación</p>
      </div>
    );
  }

  return (
    <div className="chat-container">
      {/* Header con indicador de conexión */}
      <div className="chat-header">
        <h3>💬 Chat</h3>
        <div className="connection-status">
          {isConnected ? (
            <span className="connected">🟢 Conectado</span>
          ) : (
            <span className="disconnected">🔴 Desconectado</span>
          )}
        </div>
      </div>

      {/* Lista de mensajes */}
      <div className="chat-messages">
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
                {!isOwnMessage && (
                  <img
                    src={`${apiUrl}/api/Users/${message.senderId}/profile-picture`}
                    alt={message.senderName || 'Usuario'}
                    className="message-avatar"
                    onError={(e) => {
                      (e.target as HTMLImageElement).src = '/default-avatar.png';
                    }}
                  />
                )}
                
                <div className="message-content">
                  {!isOwnMessage && (
                    <span className="message-sender">
                      {message.senderName || '[Usuario eliminado]'}
                    </span>
                  )}
                  <p className="message-text">
                    {message.content || '[Mensaje eliminado]'}
                  </p>
                  
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
                      📍 Ver ubicación
                    </a>
                  )}
                  
                  <span className="message-time">
                    {new Date(message.sentAt).toLocaleTimeString('es-ES', {
                      hour: '2-digit',
                      minute: '2-digit'
                    })}
                  </span>
                  
                  {/* Indicador de leído */}
                  {isOwnMessage && (
                    <span className="read-indicator">
                      {message.isRead ? '✓✓' : '✓'}
                    </span>
                  )}
                </div>
              </div>
            );
          })
        )}

        {/* Scroll anchor */}
        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="chat-input">
        <input
          type="text"
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyPress={handleKeyPress}
          placeholder="Escribe tu mensaje..."
          disabled={sendMessageMutation.isPending || !isConnected}
        />
        <button
          onClick={handleSend}
          disabled={!inputValue.trim() || sendMessageMutation.isPending || !isConnected}
          className="send-button"
        >
          {sendMessageMutation.isPending ? (
            <span>Enviando...</span>
          ) : (
            <span>Enviar</span>
          )}
        </button>
      </div>
    </div>
  );
};
```

---

## 🔍 Explicación Detallada

### **1. Suscripción a Nuevos Mensajes (INSERT)**

```typescript
.on(
  'postgres_changes',
  {
    event: 'INSERT',
    schema: 'public',
    table: 'Messages',
    filter: `ConversationId=eq.${conversationId}`
  },
  (payload) => {
    // ✅ Cuando se inserta un nuevo mensaje en la BD
    // Supabase notifica automáticamente
    // Agregamos el mensaje al estado
  }
)
```

**¿Qué hace?**
- Escucha cuando se **inserta** un nuevo mensaje en la tabla `Messages`
- Solo para mensajes de esta conversación (`ConversationId=eq.${conversationId}`)
- Se ejecuta **automáticamente** cuando alguien envía un mensaje
- **No necesitas hacer polling** ni refrescar manualmente

---

### **2. Suscripción a Actualizaciones (UPDATE)**

```typescript
.on(
  'postgres_changes',
  {
    event: 'UPDATE',
    schema: 'public',
    table: 'Messages',
    filter: `ConversationId=eq.${conversationId}`
  },
  (payload) => {
    // ✅ Cuando se actualiza un mensaje (ej: marcado como leído)
    // Actualizamos el mensaje en el estado
  }
)
```

**¿Qué hace?**
- Escucha cuando se **actualiza** un mensaje
- Útil para actualizar el estado de "leído" (`IsRead`)
- Se ejecuta cuando el otro usuario lee tu mensaje

---

### **3. Evitar Duplicados**

```typescript
setMessages(prev => {
  // ✅ Verificar si el mensaje ya existe
  if (prev.some(m => m.id === messageDto.id)) {
    return prev; // No agregar duplicado
  }
  
  // ✅ Agregar nuevo mensaje
  return [...prev, messageDto].sort(...);
});
```

**¿Por qué es importante?**
- A veces Supabase puede enviar el mismo evento dos veces
- Evita que aparezcan mensajes duplicados
- Verifica por ID antes de agregar

---

### **4. Limpieza de Suscripciones**

```typescript
useEffect(() => {
  // ... crear suscripción ...
  
  return () => {
    // ✅ Limpiar al desmontar el componente
    if (channelRef.current) {
      supabase.removeChannel(channelRef.current);
    }
  };
}, [conversationId]);
```

**¿Por qué es importante?**
- Evita memory leaks
- Cierra conexiones cuando el componente se desmonta
- Evita suscripciones duplicadas

---

## 🎨 Hook Personalizado (Recomendado)

Para reutilizar la lógica en múltiples componentes:

```typescript
// hooks/useRealtimeMessages.ts
import { useState, useEffect, useRef } from 'react';
import { supabase } from '@/lib/supabase';
import { Message } from '@/types/chat';

interface UseRealtimeMessagesOptions {
  conversationId: number;
  token: string;
  apiUrl: string;
  onNewMessage?: (message: Message) => void;
  onMessageUpdate?: (message: Message) => void;
}

export const useRealtimeMessages = ({
  conversationId,
  token,
  apiUrl,
  onNewMessage,
  onMessageUpdate
}: UseRealtimeMessagesOptions) => {
  const [messages, setMessages] = useState<Message[]>([]);
  const [isConnected, setIsConnected] = useState(false);
  const channelRef = useRef<any>(null);

  useEffect(() => {
    if (!conversationId) return;

    const channelName = `messages:conversation:${conversationId}`;
    const channel = supabase
      .channel(channelName)
      .on(
        'postgres_changes',
        {
          event: 'INSERT',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        async (payload) => {
          const newMessage = payload.new as any;
          
          const messageDto: Message = {
            id: newMessage.Id,
            conversationId: newMessage.ConversationId,
            senderId: newMessage.SenderId,
            content: newMessage.Content || '',
            sentAt: newMessage.SentAt,
            isRead: newMessage.IsRead || false,
            senderName: null, // Se puede obtener después
            locationLatitude: newMessage.LocationLatitude,
            locationLongitude: newMessage.LocationLongitude,
            attachmentUrls: []
          };

          setMessages(prev => {
            if (prev.some(m => m.id === messageDto.id)) {
              return prev;
            }
            const updated = [...prev, messageDto].sort((a, b) => 
              new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
            );
            return updated;
          });

          // ✅ Callback opcional
          if (onNewMessage) {
            onNewMessage(messageDto);
          }
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

          // ✅ Callback opcional
          if (onMessageUpdate) {
            const updated: Message = {
              id: updatedMessage.Id,
              conversationId: updatedMessage.ConversationId,
              senderId: updatedMessage.SenderId,
              content: updatedMessage.Content || '',
              sentAt: updatedMessage.SentAt,
              isRead: updatedMessage.IsRead,
              senderName: null,
              locationLatitude: updatedMessage.LocationLatitude,
              locationLongitude: updatedMessage.LocationLongitude,
              attachmentUrls: []
            };
            onMessageUpdate(updated);
          }
        }
      )
      .subscribe((status) => {
        setIsConnected(status === 'SUBSCRIBED');
      });

    channelRef.current = channel;

    return () => {
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current);
      }
    };
  }, [conversationId, token, apiUrl, onNewMessage, onMessageUpdate]);

  return { messages, setMessages, isConnected };
};
```

**Uso del hook:**

```typescript
// components/Chat.tsx
import { useRealtimeMessages } from '@/hooks/useRealtimeMessages';

export const Chat = ({ conversationId, token, userId, apiUrl }: ChatProps) => {
  const { messages, setMessages, isConnected } = useRealtimeMessages({
    conversationId,
    token,
    apiUrl,
    onNewMessage: (message) => {
      console.log('Nuevo mensaje:', message);
      // Hacer scroll, mostrar notificación, etc.
    }
  });

  // ... resto del componente
};
```

---

## 🐛 Manejo de Errores y Reconexión

```typescript
useEffect(() => {
  if (!conversationId) return;

  const channel = supabase
    .channel(`messages:conversation:${conversationId}`)
    .on('postgres_changes', { ... }, handleNewMessage)
    .subscribe((status) => {
      setIsConnected(status === 'SUBSCRIBED');
      
      if (status === 'CHANNEL_ERROR') {
        console.error('❌ Error en el canal');
        // ✅ Intentar reconectar después de un delay
        setTimeout(() => {
          console.log('🔄 Intentando reconectar...');
          // El canal se reconectará automáticamente
        }, 3000);
      }
    });

  return () => {
    supabase.removeChannel(channel);
  };
}, [conversationId]);
```

---

## 📊 Flujo Completo

### **Cuando TÚ envías un mensaje:**

```
1. Usuario escribe y hace clic en "Enviar"
   → POST /api/Chat/message
   → Mensaje se guarda en BD

2. Supabase detecta el INSERT en la tabla Messages
   → Envía evento a todos los suscritos al canal

3. Tu componente recibe el evento
   → Agrega el mensaje al estado
   → Aparece en la UI inmediatamente

4. El otro usuario (si está conectado) también recibe el evento
   → Ve tu mensaje en tiempo real
```

### **Cuando RECIBES un mensaje:**

```
1. Otro usuario envía mensaje
   → POST /api/Chat/message
   → Mensaje se guarda en BD

2. Supabase detecta el INSERT
   → Envía evento a tu componente

3. Tu componente recibe el evento
   → Agrega el mensaje al estado
   → Aparece en la UI inmediatamente
   → Scroll automático al final
```

---

## ✅ Checklist de Implementación

- [ ] Instalar `@supabase/supabase-js`
- [ ] Configurar cliente Supabase
- [ ] Crear componente de chat
- [ ] Implementar suscripción a INSERT (nuevos mensajes)
- [ ] Implementar suscripción a UPDATE (mensajes actualizados)
- [ ] Agregar lógica para evitar duplicados
- [ ] Implementar limpieza de suscripciones
- [ ] Agregar indicador de conexión
- [ ] Probar envío de mensajes
- [ ] Probar recepción de mensajes
- [ ] Probar con múltiples usuarios

---

## 🎯 Resumen

### **Para que los mensajes aparezcan al momento:**

1. ✅ **Suscripción a Supabase Realtime** - Escucha cambios en la tabla `Messages`
2. ✅ **Evento INSERT** - Detecta nuevos mensajes automáticamente
3. ✅ **Actualizar estado** - Agrega mensajes al estado de React
4. ✅ **Sin polling** - No necesitas hacer `setInterval` ni refrescar manualmente

### **Ventajas:**

- ⚡ **Instantáneo** - Los mensajes aparecen en tiempo real
- 🔄 **Automático** - No necesitas recargar ni hacer polling
- 💪 **Robusto** - Supabase maneja la reconexión automáticamente
- 🎨 **Simple** - Solo necesitas suscribirte a los eventos

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Listo para implementar
