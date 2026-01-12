# Guía de Implementación: Chat con Supabase Realtime

## Resumen

El sistema de chat ha sido migrado de SignalR a **Supabase Realtime**. Esta guía explica cómo implementar el cliente en el frontend.

---

## Configuración Inicial

### 1. Instalar Supabase Client

```bash
npm install @supabase/supabase-js
# o
yarn add @supabase/supabase-js
```

### 2. Configurar el Cliente Supabase

```typescript
// lib/supabase.ts
import { createClient } from '@supabase/supabase-js'

const SUPABASE_URL = 'https://rveqsehzlvbttlpmsbmi.supabase.co'

// Puedes usar cualquiera de estas claves:
// - Publishable Key (recomendada): 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'
// - Legacy Anon Key: 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InJ2ZXFzZWh6bHZidHRscG1zYm1pIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njc0NDkyMTcsImV4cCI6MjA4MzAyNTIxN30.LA_zA1QezNnVU2dsojD6adI01V3ZN3uUNU1rB78DqF8'
const SUPABASE_ANON_KEY = 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'

export const supabase = createClient(SUPABASE_URL, SUPABASE_ANON_KEY)
```

---

## Escuchar Nuevos Mensajes

### Opción 1: Postgres Changes (Recomendado)

Supabase escucha los cambios directamente en la tabla `Messages`:

```typescript
// hooks/useConversationMessages.ts
import { useEffect, useState } from 'react'
import { supabase } from '@/lib/supabase'

interface Message {
  Id: number
  ConversationId: number
  SenderId: number | null
  Content: string | null
  SentAt: string
  IsRead: boolean
  LocationLatitude: string | null
  LocationLongitude: string | null
}

export function useConversationMessages(conversationId: number) {
  const [messages, setMessages] = useState<Message[]>([])

  useEffect(() => {
    // Suscribirse a cambios en la tabla Messages para esta conversación
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
          console.log('Nuevo mensaje recibido:', payload.new)
          setMessages(prev => [...prev, payload.new as Message])
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
          console.log('Mensaje actualizado:', payload.new)
          setMessages(prev => 
            prev.map(msg => msg.Id === (payload.new as Message).Id ? payload.new as Message : msg)
          )
        }
      )
      .subscribe()

    // Cleanup al desmontar
    return () => {
      supabase.removeChannel(channel)
    }
  }, [conversationId])

  return messages
}
```

### Opción 2: Broadcast (Para notificaciones adicionales)

El backend también envía broadcasts que puedes escuchar:

```typescript
// hooks/useChatBroadcast.ts
import { useEffect } from 'react'
import { supabase } from '@/lib/supabase'

export function useChatBroadcast(
  conversationId: number, 
  onNewMessage: (message: any) => void
) {
  useEffect(() => {
    const channel = supabase
      .channel(`conversation:${conversationId}`)
      .on('broadcast', { event: 'new_message' }, ({ payload }) => {
        console.log('Broadcast de nuevo mensaje:', payload)
        onNewMessage(payload)
      })
      .subscribe()

    return () => {
      supabase.removeChannel(channel)
    }
  }, [conversationId, onNewMessage])
}
```

---

## Indicador de Typing

### Enviar Notificación de Typing

```typescript
// api/chat.ts
const API_BASE_URL = process.env.REACT_APP_API_URL || 'http://localhost:7124'

export async function notifyTyping(conversationId: number, isTyping: boolean, token: string) {
  const response = await fetch(`${API_BASE_URL}/api/Chat/typing`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${token}`
    },
    body: JSON.stringify({ conversationId, isTyping })
  })
  return response.ok
}
```

### Escuchar Typing de Otros Usuarios

```typescript
// hooks/useTypingIndicator.ts
import { useEffect, useState } from 'react'
import { supabase } from '@/lib/supabase'

interface TypingUser {
  userId: number
  isTyping: boolean
  timestamp: string
}

export function useTypingIndicator(conversationId: number, currentUserId: number) {
  const [typingUsers, setTypingUsers] = useState<Map<number, boolean>>(new Map())

  useEffect(() => {
    const channel = supabase
      .channel(`conversation:${conversationId}`)
      .on('broadcast', { event: 'typing' }, ({ payload }) => {
        const { userId, isTyping } = payload as TypingUser
        
        // No mostrar nuestro propio indicador de typing
        if (userId === currentUserId) return
        
        setTypingUsers(prev => {
          const newMap = new Map(prev)
          if (isTyping) {
            newMap.set(userId, true)
          } else {
            newMap.delete(userId)
          }
          return newMap
        })
      })
      .subscribe()

    return () => {
      supabase.removeChannel(channel)
    }
  }, [conversationId, currentUserId])

  return Array.from(typingUsers.keys())
}
```

---

## Presence (Usuarios Online)

### Rastrear Presencia de Usuarios

```typescript
// hooks/usePresence.ts
import { useEffect, useState } from 'react'
import { supabase } from '@/lib/supabase'

interface PresenceState {
  [key: string]: {
    user_id: number
    online_at: string
  }[]
}

export function useConversationPresence(conversationId: number, userId: number) {
  const [onlineUsers, setOnlineUsers] = useState<number[]>([])

  useEffect(() => {
    const channel = supabase.channel(`room:${conversationId}`)
    
    channel
      .on('presence', { event: 'sync' }, () => {
        const state = channel.presenceState() as PresenceState
        const users = Object.values(state)
          .flat()
          .map(p => p.user_id)
        setOnlineUsers([...new Set(users)])
      })
      .on('presence', { event: 'join' }, ({ key, newPresences }) => {
        console.log('Usuario se unió:', newPresences)
      })
      .on('presence', { event: 'leave' }, ({ key, leftPresences }) => {
        console.log('Usuario salió:', leftPresences)
      })
      .subscribe(async (status) => {
        if (status === 'SUBSCRIBED') {
          // Registrar nuestra presencia
          await channel.track({
            user_id: userId,
            online_at: new Date().toISOString()
          })
        }
      })

    return () => {
      supabase.removeChannel(channel)
    }
  }, [conversationId, userId])

  return onlineUsers
}
```

---

## Ejemplo Completo: Componente de Chat

```tsx
// components/Chat.tsx
import React, { useState, useEffect, useCallback } from 'react'
import { supabase } from '@/lib/supabase'
import { useConversationMessages } from '@/hooks/useConversationMessages'
import { useTypingIndicator } from '@/hooks/useTypingIndicator'
import { useConversationPresence } from '@/hooks/usePresence'
import { notifyTyping } from '@/api/chat'

interface ChatProps {
  conversationId: number
  userId: number
  token: string
}

export function Chat({ conversationId, userId, token }: ChatProps) {
  const [inputValue, setInputValue] = useState('')
  const [isTyping, setIsTyping] = useState(false)
  
  // Hooks de Supabase Realtime
  const messages = useConversationMessages(conversationId)
  const typingUserIds = useTypingIndicator(conversationId, userId)
  const onlineUsers = useConversationPresence(conversationId, userId)

  // Debounce para typing indicator
  useEffect(() => {
    let timeout: NodeJS.Timeout
    
    if (inputValue.length > 0 && !isTyping) {
      setIsTyping(true)
      notifyTyping(conversationId, true, token)
    }
    
    timeout = setTimeout(() => {
      if (isTyping) {
        setIsTyping(false)
        notifyTyping(conversationId, false, token)
      }
    }, 2000)

    return () => clearTimeout(timeout)
  }, [inputValue, conversationId, token, isTyping])

  // Enviar mensaje via API REST
  const sendMessage = async () => {
    if (!inputValue.trim()) return

    const formData = new FormData()
    formData.append('ConversationId', conversationId.toString())
    formData.append('Content', inputValue)

    try {
      const response = await fetch(`${process.env.REACT_APP_API_URL}/api/Chat/message`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`
        },
        body: formData
      })

      if (response.ok) {
        setInputValue('')
        setIsTyping(false)
        notifyTyping(conversationId, false, token)
      }
    } catch (error) {
      console.error('Error enviando mensaje:', error)
    }
  }

  return (
    <div className="chat-container">
      {/* Indicador de usuarios online */}
      <div className="online-users">
        {onlineUsers.length} usuario(s) online
      </div>

      {/* Lista de mensajes */}
      <div className="messages">
        {messages.map(msg => (
          <div 
            key={msg.Id} 
            className={`message ${msg.SenderId === userId ? 'own' : 'other'}`}
          >
            <p>{msg.Content}</p>
            <span className="time">
              {new Date(msg.SentAt).toLocaleTimeString()}
            </span>
          </div>
        ))}
      </div>

      {/* Indicador de typing */}
      {typingUserIds.length > 0 && (
        <div className="typing-indicator">
          {typingUserIds.length === 1 
            ? 'Alguien está escribiendo...' 
            : `${typingUserIds.length} personas están escribiendo...`}
        </div>
      )}

      {/* Input de mensaje */}
      <div className="message-input">
        <input
          type="text"
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyPress={(e) => e.key === 'Enter' && sendMessage()}
          placeholder="Escribe un mensaje..."
        />
        <button onClick={sendMessage}>Enviar</button>
      </div>
    </div>
  )
}
```

---

## Migración desde SignalR

### Antes (SignalR)

```typescript
// Conexión SignalR (YA NO USAR)
import * as signalR from "@microsoft/signalr"

const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${API_URL}/chatHub?access_token=${token}`)
  .build()

connection.on("ReceiveMessage", (message) => { ... })
connection.invoke("JoinConversation", conversationId)
```

### Después (Supabase Realtime)

```typescript
// Suscripción Supabase Realtime
import { supabase } from '@/lib/supabase'

const channel = supabase
  .channel(`conversation:${conversationId}`)
  .on('postgres_changes', {
    event: 'INSERT',
    schema: 'public',
    table: 'Messages',
    filter: `ConversationId=eq.${conversationId}`
  }, (payload) => {
    // Nuevo mensaje recibido
  })
  .subscribe()
```

---

## Comparación: SignalR vs Supabase Realtime

| Característica | SignalR (Anterior) | Supabase Realtime (Nuevo) |
|----------------|-------------------|---------------------------|
| Conexión | WebSocket a `/chatHub` | WebSocket a Supabase |
| Nuevos mensajes | `ReceiveMessage` event | `postgres_changes` INSERT |
| Typing indicator | `UserTyping` method | Broadcast event `typing` |
| Presencia | Diccionarios en memoria | Presence API integrada |
| Reconexión | Manual | Automática |
| Escalabilidad | Redis backplane | Global automático |

---

## Eventos del Backend

El backend envía estos broadcasts a los canales:

| Canal | Evento | Payload |
|-------|--------|---------|
| `conversation:{id}` | `new_message` | `MessageDto` completo |
| `conversation:{id}` | `typing` | `{ userId, isTyping, timestamp }` |
| `conversation:{id}` | `message_read` | `{ messageId, conversationId }` |
| `conversation:{id}` | `deliverable_uploaded` | `DeliverableResponseDto` |
| `conversation:{id}` | `user_joined` | `{ userId, isOnline }` |
| `conversation:{id}` | `user_left` | `{ userId, isOnline }` |

---

## Troubleshooting

### El canal no recibe mensajes

1. Verificar que RLS esté configurado correctamente
2. Asegurarse de que la tabla está en la publicación `supabase_realtime`
3. Verificar el filtro del canal

### Presencia no funciona

1. Asegurarse de llamar `channel.track()` después de suscribirse
2. Verificar que el status sea `'SUBSCRIBED'` antes de trackear

### Typing indicator con delay

1. Implementar debounce en el frontend (2 segundos recomendado)
2. No enviar notificación en cada keystroke

---

## Endpoints REST del Backend

| Método | Endpoint | Descripción |
|--------|----------|-------------|
| GET | `/api/Chat/conversation?searchId={id}` | Obtener/crear conversación |
| GET | `/api/Chat/by-searchhire/{id}` | Obtener por SearchHireId |
| POST | `/api/Chat/message` | Enviar mensaje (FormData) |
| PUT | `/api/Chat/message/{id}/read` | Marcar como leído |
| POST | `/api/Chat/typing` | Notificar typing |
| POST | `/api/Chat/deliverable/{id}` | Subir entregable |
| GET | `/api/Chat/deliverable/{id}` | Obtener entregables |

---

## Recursos

- [Supabase Realtime Docs](https://supabase.com/docs/guides/realtime)
- [Supabase Presence](https://supabase.com/docs/guides/realtime/presence)
- [Supabase Broadcast](https://supabase.com/docs/guides/realtime/broadcast)
- [Postgres Changes](https://supabase.com/docs/guides/realtime/postgres-changes)
