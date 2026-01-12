# 🚀 Guía Completa: Implementación del Chat con Supabase Realtime

## 📋 Índice
1. [Instalación y Configuración](#1-instalación-y-configuración)
2. [Estructura de Archivos Recomendada](#2-estructura-de-archivos-recomendada)
3. [Configuración del Cliente Supabase](#3-configuración-del-cliente-supabase)
4. [Tipos TypeScript](#4-tipos-typescript)
5. [Servicios de API REST](#5-servicios-de-api-rest)
6. [Hooks de Supabase Realtime](#6-hooks-de-supabase-realtime)
7. [Componentes de Chat](#7-componentes-de-chat)
8. [Integración Completa](#8-integración-completa)
9. [Manejo de Errores y Reconexión](#9-manejo-de-errores-y-reconexión)
10. [Testing y Debugging](#10-testing-y-debugging)

---

## 1. Instalación y Configuración

### Instalar dependencias

```bash
# Con npm
npm install @supabase/supabase-js

# Con yarn
yarn add @supabase/supabase-js

# Con pnpm
pnpm add @supabase/supabase-js
```

### Variables de entorno

Crear archivo `.env` o `.env.local`:

```env
# Supabase
VITE_SUPABASE_URL=https://rveqsehzlvbttlpmsbmi.supabase.co
VITE_SUPABASE_ANON_KEY=sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0

# API Backend
VITE_API_URL=http://localhost:7124
```

> **Nota:** Si usas Create React App, las variables deben empezar con `REACT_APP_` en lugar de `VITE_`.

---

## 2. Estructura de Archivos Recomendada

```
src/
├── lib/
│   └── supabase.ts              # Cliente Supabase
├── types/
│   └── chat.types.ts            # Tipos TypeScript
├── services/
│   └── chatService.ts           # Llamadas a la API REST
├── hooks/
│   ├── useConversation.ts       # Hook principal de conversación
│   ├── useMessages.ts           # Hook para escuchar mensajes
│   ├── useTypingIndicator.ts    # Hook para "escribiendo..."
│   └── usePresence.ts           # Hook para usuarios online
├── components/
│   └── chat/
│       ├── Chat.tsx             # Componente principal
│       ├── MessageList.tsx      # Lista de mensajes
│       ├── MessageItem.tsx      # Mensaje individual
│       ├── MessageInput.tsx     # Input de mensaje
│       ├── TypingIndicator.tsx  # "Usuario está escribiendo..."
│       └── OnlineUsers.tsx      # Indicador de usuarios online
└── context/
    └── ChatContext.tsx          # Context para estado global del chat
```

---

## 3. Configuración del Cliente Supabase

### `src/lib/supabase.ts`

```typescript
import { createClient, SupabaseClient } from '@supabase/supabase-js'

const supabaseUrl = import.meta.env.VITE_SUPABASE_URL
const supabaseAnonKey = import.meta.env.VITE_SUPABASE_ANON_KEY

if (!supabaseUrl || !supabaseAnonKey) {
  throw new Error('Missing Supabase environment variables')
}

export const supabase: SupabaseClient = createClient(supabaseUrl, supabaseAnonKey, {
  realtime: {
    params: {
      eventsPerSecond: 10 // Límite de eventos por segundo
    }
  }
})

// Helper para verificar conexión
export const checkRealtimeConnection = async (): Promise<boolean> => {
  return new Promise((resolve) => {
    const channel = supabase.channel('connection-test')
    
    channel.subscribe((status) => {
      if (status === 'SUBSCRIBED') {
        supabase.removeChannel(channel)
        resolve(true)
      } else if (status === 'CHANNEL_ERROR' || status === 'TIMED_OUT') {
        supabase.removeChannel(channel)
        resolve(false)
      }
    })

    // Timeout de 5 segundos
    setTimeout(() => {
      supabase.removeChannel(channel)
      resolve(false)
    }, 5000)
  })
}
```

---

## 4. Tipos TypeScript

### `src/types/chat.types.ts`

```typescript
// ==========================================
// TIPOS DE LA BASE DE DATOS (Supabase)
// ==========================================

/** Mensaje tal como viene de la base de datos */
export interface DBMessage {
  Id: number
  ConversationId: number
  SenderId: number | null
  Content: string | null
  SentAt: string
  IsRead: boolean
  LocationLatitude: string | null
  LocationLongitude: string | null
}

/** Conversación tal como viene de la base de datos */
export interface DBConversation {
  Id: number
  SearchHireId: number
  ClientId: number | null
  ExpertId: number | null
  IsActive: boolean
  CreatedAt: string
  UpdatedAt: string
}

// ==========================================
// TIPOS DE LA API REST (Backend)
// ==========================================

/** Mensaje tal como viene de la API REST */
export interface MessageDto {
  Id: number
  ConversationId: number
  SenderId: number | null
  Content: string | null
  SentAt: string
  IsRead: boolean
  SenderName: string | null
  LocationLatitude: string | null
  LocationLongitude: string | null
  AttachmentUrls: string[]
}

/** Conversación tal como viene de la API REST */
export interface ConversationDto {
  Id: number
  SearchHireId: number
  ClientId: number | null
  ExpertId: number | null
  IsActive: boolean
  CreatedAt: string
  UpdatedAt: string
  Messages: MessageDto[]
}

// ==========================================
// TIPOS PARA ENVIAR MENSAJES
// ==========================================

/** DTO para enviar un nuevo mensaje */
export interface SendMessageDto {
  ConversationId: number
  Content?: string
  LocationLatitude?: string
  LocationLongitude?: string
  Attachments?: File[]
}

/** DTO para notificar typing */
export interface TypingNotificationDto {
  ConversationId: number
  IsTyping: boolean
}

// ==========================================
// TIPOS DE EVENTOS REALTIME
// ==========================================

/** Payload del evento typing */
export interface TypingPayload {
  userId: number
  conversationId: number
  isTyping: boolean
  timestamp: string
}

/** Payload del evento de presencia */
export interface PresencePayload {
  userId: number
  conversationId: number
  isOnline: boolean
  timestamp: string
}

/** Payload del evento message_read */
export interface MessageReadPayload {
  messageId: number
  conversationId: number
}

/** Estado de presencia de un usuario */
export interface PresenceState {
  user_id: number
  online_at: string
}

// ==========================================
// TIPOS DE ESTADO DEL COMPONENTE
// ==========================================

/** Estado del chat */
export interface ChatState {
  conversation: ConversationDto | null
  messages: MessageDto[]
  isLoading: boolean
  error: string | null
  typingUsers: number[]
  onlineUsers: number[]
  isConnected: boolean
}

/** Acciones del chat */
export type ChatAction =
  | { type: 'SET_CONVERSATION'; payload: ConversationDto }
  | { type: 'ADD_MESSAGE'; payload: MessageDto }
  | { type: 'UPDATE_MESSAGE'; payload: MessageDto }
  | { type: 'SET_LOADING'; payload: boolean }
  | { type: 'SET_ERROR'; payload: string | null }
  | { type: 'SET_TYPING_USERS'; payload: number[] }
  | { type: 'SET_ONLINE_USERS'; payload: number[] }
  | { type: 'SET_CONNECTED'; payload: boolean }
```

---

## 5. Servicios de API REST

### `src/services/chatService.ts`

```typescript
import type {
  ConversationDto,
  MessageDto,
  SendMessageDto,
  TypingNotificationDto
} from '../types/chat.types'

const API_URL = import.meta.env.VITE_API_URL || 'http://localhost:7124'

/** Headers con autenticación */
const getAuthHeaders = (token: string): HeadersInit => ({
  'Authorization': `Bearer ${token}`,
  'Content-Type': 'application/json'
})

/** Manejo de errores de la API */
const handleApiError = async (response: Response): Promise<never> => {
  let errorMessage = `Error ${response.status}: ${response.statusText}`
  
  try {
    const errorData = await response.json()
    errorMessage = errorData.message || errorMessage
  } catch {
    // Si no puede parsear JSON, usar mensaje por defecto
  }
  
  throw new Error(errorMessage)
}

// ==========================================
// CONVERSACIONES
// ==========================================

/**
 * Obtener o crear una conversación por SearchId
 */
export const getConversation = async (
  searchId: number,
  token: string
): Promise<ConversationDto> => {
  const response = await fetch(
    `${API_URL}/api/Chat/conversation?searchId=${searchId}`,
    {
      method: 'GET',
      headers: getAuthHeaders(token)
    }
  )

  if (!response.ok) {
    await handleApiError(response)
  }

  return response.json()
}

/**
 * Obtener conversación por SearchHireId
 * Útil cuando el Search fue eliminado pero el SearchHire existe
 */
export const getConversationBySearchHireId = async (
  searchHireId: number,
  token: string
): Promise<ConversationDto> => {
  const response = await fetch(
    `${API_URL}/api/Chat/by-searchhire/${searchHireId}`,
    {
      method: 'GET',
      headers: getAuthHeaders(token)
    }
  )

  if (!response.ok) {
    await handleApiError(response)
  }

  return response.json()
}

// ==========================================
// MENSAJES
// ==========================================

/**
 * Enviar un mensaje de texto
 */
export const sendMessage = async (
  dto: SendMessageDto,
  token: string
): Promise<MessageDto> => {
  const formData = new FormData()
  formData.append('ConversationId', dto.ConversationId.toString())
  
  if (dto.Content) {
    formData.append('Content', dto.Content)
  }
  
  if (dto.LocationLatitude) {
    formData.append('LocationLatitude', dto.LocationLatitude)
  }
  
  if (dto.LocationLongitude) {
    formData.append('LocationLongitude', dto.LocationLongitude)
  }
  
  if (dto.Attachments) {
    dto.Attachments.forEach((file) => {
      formData.append('Attachments', file)
    })
  }

  const response = await fetch(`${API_URL}/api/Chat/message`, {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`
      // NO incluir Content-Type para FormData, el browser lo añade automáticamente
    },
    body: formData
  })

  if (!response.ok) {
    await handleApiError(response)
  }

  return response.json()
}

/**
 * Marcar un mensaje como leído
 */
export const markMessageAsRead = async (
  messageId: number,
  token: string
): Promise<void> => {
  const response = await fetch(
    `${API_URL}/api/Chat/message/${messageId}/read`,
    {
      method: 'PUT',
      headers: getAuthHeaders(token)
    }
  )

  if (!response.ok) {
    await handleApiError(response)
  }
}

// ==========================================
// TYPING INDICATOR
// ==========================================

/**
 * Notificar que el usuario está escribiendo
 */
export const notifyTyping = async (
  dto: TypingNotificationDto,
  token: string
): Promise<void> => {
  const response = await fetch(`${API_URL}/api/Chat/typing`, {
    method: 'POST',
    headers: getAuthHeaders(token),
    body: JSON.stringify(dto)
  })

  if (!response.ok) {
    // No lanzar error para typing, solo loggear
    console.warn('Failed to send typing notification')
  }
}

// ==========================================
// ENTREGABLES
// ==========================================

/**
 * Subir un entregable (solo expertos)
 */
export const uploadDeliverable = async (
  searchHireId: number,
  files: File[],
  token: string
): Promise<{ message: string; deliverable: any }> => {
  const formData = new FormData()
  files.forEach((file) => {
    formData.append('Files', file)
  })

  const response = await fetch(
    `${API_URL}/api/Chat/deliverable/${searchHireId}`,
    {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${token}`
      },
      body: formData
    }
  )

  if (!response.ok) {
    await handleApiError(response)
  }

  return response.json()
}

/**
 * Obtener entregables de un SearchHire
 */
export const getDeliverables = async (
  searchHireId: number,
  token: string
): Promise<{ message: string; deliverable: any }> => {
  const response = await fetch(
    `${API_URL}/api/Chat/deliverable/${searchHireId}`,
    {
      method: 'GET',
      headers: getAuthHeaders(token)
    }
  )

  if (!response.ok) {
    await handleApiError(response)
  }

  return response.json()
}
```

---

## 6. Hooks de Supabase Realtime

### `src/hooks/useMessages.ts` - Escuchar nuevos mensajes

```typescript
import { useEffect, useCallback, useRef } from 'react'
import { RealtimeChannel } from '@supabase/supabase-js'
import { supabase } from '../lib/supabase'
import type { DBMessage, MessageDto } from '../types/chat.types'

interface UseMessagesProps {
  conversationId: number | null
  onNewMessage: (message: MessageDto) => void
  onMessageUpdated: (message: MessageDto) => void
  enabled?: boolean
}

/**
 * Hook para escuchar nuevos mensajes en tiempo real
 * Usa Postgres Changes para detectar INSERTs y UPDATEs en la tabla Messages
 */
export function useMessages({
  conversationId,
  onNewMessage,
  onMessageUpdated,
  enabled = true
}: UseMessagesProps) {
  const channelRef = useRef<RealtimeChannel | null>(null)

  // Convertir mensaje de DB a DTO (sin SenderName, que vendrá del backend)
  const convertDbMessageToDto = useCallback((dbMessage: DBMessage): MessageDto => {
    return {
      Id: dbMessage.Id,
      ConversationId: dbMessage.ConversationId,
      SenderId: dbMessage.SenderId,
      Content: dbMessage.Content,
      SentAt: dbMessage.SentAt,
      IsRead: dbMessage.IsRead,
      SenderName: null, // Se actualiza después desde el estado local
      LocationLatitude: dbMessage.LocationLatitude,
      LocationLongitude: dbMessage.LocationLongitude,
      AttachmentUrls: [] // Los attachments vienen por otra tabla
    }
  }, [])

  useEffect(() => {
    if (!conversationId || !enabled) {
      return
    }

    // Crear canal con nombre único
    const channelName = `messages:${conversationId}:${Date.now()}`
    
    console.log(`📡 Suscribiéndose a mensajes de conversación ${conversationId}`)

    const channel = supabase
      .channel(channelName)
      // Escuchar nuevos mensajes
      .on(
        'postgres_changes',
        {
          event: 'INSERT',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        (payload) => {
          console.log('📩 Nuevo mensaje recibido:', payload.new)
          const messageDto = convertDbMessageToDto(payload.new as DBMessage)
          onNewMessage(messageDto)
        }
      )
      // Escuchar actualizaciones (ej: IsRead cambia a true)
      .on(
        'postgres_changes',
        {
          event: 'UPDATE',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        (payload) => {
          console.log('✏️ Mensaje actualizado:', payload.new)
          const messageDto = convertDbMessageToDto(payload.new as DBMessage)
          onMessageUpdated(messageDto)
        }
      )
      .subscribe((status) => {
        console.log(`📡 Estado de suscripción a mensajes: ${status}`)
      })

    channelRef.current = channel

    // Cleanup al desmontar o cambiar de conversación
    return () => {
      console.log(`🔌 Desuscribiéndose de mensajes de conversación ${conversationId}`)
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current)
        channelRef.current = null
      }
    }
  }, [conversationId, enabled, onNewMessage, onMessageUpdated, convertDbMessageToDto])

  // Función para forzar reconexión
  const reconnect = useCallback(() => {
    if (channelRef.current) {
      channelRef.current.unsubscribe()
      channelRef.current.subscribe()
    }
  }, [])

  return { reconnect }
}
```

### `src/hooks/useTypingIndicator.ts` - Indicador de escritura

```typescript
import { useEffect, useState, useCallback, useRef } from 'react'
import { RealtimeChannel } from '@supabase/supabase-js'
import { supabase } from '../lib/supabase'
import { notifyTyping } from '../services/chatService'
import type { TypingPayload } from '../types/chat.types'

interface UseTypingIndicatorProps {
  conversationId: number | null
  currentUserId: number
  token: string
  enabled?: boolean
}

interface UseTypingIndicatorReturn {
  typingUsers: number[]
  startTyping: () => void
  stopTyping: () => void
}

/**
 * Hook para manejar el indicador de "escribiendo..."
 * - Escucha cuando otros usuarios están escribiendo
 * - Envía notificaciones cuando el usuario actual está escribiendo
 */
export function useTypingIndicator({
  conversationId,
  currentUserId,
  token,
  enabled = true
}: UseTypingIndicatorProps): UseTypingIndicatorReturn {
  const [typingUsers, setTypingUsers] = useState<number[]>([])
  const channelRef = useRef<RealtimeChannel | null>(null)
  const typingTimeoutRef = useRef<NodeJS.Timeout | null>(null)
  const isTypingRef = useRef(false)

  // Limpiar usuarios que dejaron de escribir después de 3 segundos
  const typingTimeouts = useRef<Map<number, NodeJS.Timeout>>(new Map())

  useEffect(() => {
    if (!conversationId || !enabled) {
      return
    }

    const channelName = `typing:${conversationId}`

    const channel = supabase
      .channel(channelName)
      .on('broadcast', { event: 'typing' }, ({ payload }) => {
        const typingData = payload as TypingPayload
        
        // Ignorar nuestro propio evento de typing
        if (typingData.userId === currentUserId) {
          return
        }

        if (typingData.isTyping) {
          // Agregar usuario a la lista
          setTypingUsers(prev => {
            if (!prev.includes(typingData.userId)) {
              return [...prev, typingData.userId]
            }
            return prev
          })

          // Limpiar timeout anterior si existe
          const existingTimeout = typingTimeouts.current.get(typingData.userId)
          if (existingTimeout) {
            clearTimeout(existingTimeout)
          }

          // Configurar timeout para remover después de 3 segundos sin actividad
          const timeout = setTimeout(() => {
            setTypingUsers(prev => prev.filter(id => id !== typingData.userId))
            typingTimeouts.current.delete(typingData.userId)
          }, 3000)

          typingTimeouts.current.set(typingData.userId, timeout)
        } else {
          // Remover usuario de la lista
          setTypingUsers(prev => prev.filter(id => id !== typingData.userId))
          
          const existingTimeout = typingTimeouts.current.get(typingData.userId)
          if (existingTimeout) {
            clearTimeout(existingTimeout)
            typingTimeouts.current.delete(typingData.userId)
          }
        }
      })
      .subscribe()

    channelRef.current = channel

    return () => {
      // Limpiar todos los timeouts
      typingTimeouts.current.forEach(timeout => clearTimeout(timeout))
      typingTimeouts.current.clear()
      
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current)
        channelRef.current = null
      }
    }
  }, [conversationId, currentUserId, enabled])

  // Notificar que empezamos a escribir
  const startTyping = useCallback(() => {
    if (!conversationId || isTypingRef.current) {
      return
    }

    isTypingRef.current = true
    notifyTyping({ ConversationId: conversationId, IsTyping: true }, token)

    // Auto-stop después de 2 segundos sin actividad
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current)
    }

    typingTimeoutRef.current = setTimeout(() => {
      stopTyping()
    }, 2000)
  }, [conversationId, token])

  // Notificar que dejamos de escribir
  const stopTyping = useCallback(() => {
    if (!conversationId || !isTypingRef.current) {
      return
    }

    isTypingRef.current = false
    notifyTyping({ ConversationId: conversationId, IsTyping: false }, token)

    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current)
      typingTimeoutRef.current = null
    }
  }, [conversationId, token])

  return {
    typingUsers,
    startTyping,
    stopTyping
  }
}
```

### `src/hooks/usePresence.ts` - Usuarios online

```typescript
import { useEffect, useState, useCallback, useRef } from 'react'
import { RealtimeChannel } from '@supabase/supabase-js'
import { supabase } from '../lib/supabase'
import type { PresenceState } from '../types/chat.types'

interface UsePresenceProps {
  conversationId: number | null
  userId: number
  enabled?: boolean
}

interface UsePresenceReturn {
  onlineUsers: number[]
  isConnected: boolean
}

/**
 * Hook para rastrear usuarios online en una conversación
 * Usa Supabase Presence para tracking en tiempo real
 */
export function usePresence({
  conversationId,
  userId,
  enabled = true
}: UsePresenceProps): UsePresenceReturn {
  const [onlineUsers, setOnlineUsers] = useState<number[]>([])
  const [isConnected, setIsConnected] = useState(false)
  const channelRef = useRef<RealtimeChannel | null>(null)

  useEffect(() => {
    if (!conversationId || !userId || !enabled) {
      return
    }

    const channelName = `presence:${conversationId}`

    const channel = supabase.channel(channelName, {
      config: {
        presence: {
          key: userId.toString()
        }
      }
    })

    channel
      .on('presence', { event: 'sync' }, () => {
        const state = channel.presenceState()
        
        // Extraer IDs de usuarios únicos
        const users: number[] = []
        Object.values(state).forEach((presences) => {
          (presences as PresenceState[]).forEach((presence) => {
            if (!users.includes(presence.user_id)) {
              users.push(presence.user_id)
            }
          })
        })
        
        setOnlineUsers(users)
        console.log('👥 Usuarios online:', users)
      })
      .on('presence', { event: 'join' }, ({ key, newPresences }) => {
        console.log(`👋 Usuario ${key} se unió:`, newPresences)
      })
      .on('presence', { event: 'leave' }, ({ key, leftPresences }) => {
        console.log(`👋 Usuario ${key} salió:`, leftPresences)
      })
      .subscribe(async (status) => {
        if (status === 'SUBSCRIBED') {
          // Registrar nuestra presencia
          await channel.track({
            user_id: userId,
            online_at: new Date().toISOString()
          })
          setIsConnected(true)
          console.log('✅ Presencia registrada')
        } else if (status === 'CHANNEL_ERROR') {
          setIsConnected(false)
          console.error('❌ Error en canal de presencia')
        }
      })

    channelRef.current = channel

    return () => {
      if (channelRef.current) {
        channelRef.current.untrack()
        supabase.removeChannel(channelRef.current)
        channelRef.current = null
      }
      setIsConnected(false)
    }
  }, [conversationId, userId, enabled])

  return {
    onlineUsers,
    isConnected
  }
}
```

### `src/hooks/useConversation.ts` - Hook principal

```typescript
import { useState, useEffect, useCallback, useReducer } from 'react'
import { getConversation, sendMessage, markMessageAsRead } from '../services/chatService'
import { useMessages } from './useMessages'
import { useTypingIndicator } from './useTypingIndicator'
import { usePresence } from './usePresence'
import type { 
  ChatState, 
  ChatAction, 
  MessageDto, 
  SendMessageDto,
  ConversationDto 
} from '../types/chat.types'

// Reducer para manejar el estado del chat
function chatReducer(state: ChatState, action: ChatAction): ChatState {
  switch (action.type) {
    case 'SET_CONVERSATION':
      return {
        ...state,
        conversation: action.payload,
        messages: action.payload.Messages,
        isLoading: false,
        error: null
      }
    case 'ADD_MESSAGE':
      // Evitar duplicados
      if (state.messages.some(m => m.Id === action.payload.Id)) {
        return state
      }
      return {
        ...state,
        messages: [...state.messages, action.payload]
      }
    case 'UPDATE_MESSAGE':
      return {
        ...state,
        messages: state.messages.map(m =>
          m.Id === action.payload.Id ? { ...m, ...action.payload } : m
        )
      }
    case 'SET_LOADING':
      return { ...state, isLoading: action.payload }
    case 'SET_ERROR':
      return { ...state, error: action.payload, isLoading: false }
    case 'SET_TYPING_USERS':
      return { ...state, typingUsers: action.payload }
    case 'SET_ONLINE_USERS':
      return { ...state, onlineUsers: action.payload }
    case 'SET_CONNECTED':
      return { ...state, isConnected: action.payload }
    default:
      return state
  }
}

const initialState: ChatState = {
  conversation: null,
  messages: [],
  isLoading: true,
  error: null,
  typingUsers: [],
  onlineUsers: [],
  isConnected: false
}

interface UseConversationProps {
  searchId: number
  userId: number
  token: string
}

/**
 * Hook principal que combina todos los hooks de chat
 */
export function useConversation({ searchId, userId, token }: UseConversationProps) {
  const [state, dispatch] = useReducer(chatReducer, initialState)
  const [isSending, setIsSending] = useState(false)

  // Cargar conversación inicial
  useEffect(() => {
    let cancelled = false

    const loadConversation = async () => {
      dispatch({ type: 'SET_LOADING', payload: true })
      
      try {
        const conversation = await getConversation(searchId, token)
        
        if (!cancelled) {
          dispatch({ type: 'SET_CONVERSATION', payload: conversation })
        }
      } catch (error) {
        if (!cancelled) {
          dispatch({ 
            type: 'SET_ERROR', 
            payload: error instanceof Error ? error.message : 'Error cargando conversación' 
          })
        }
      }
    }

    loadConversation()

    return () => {
      cancelled = true
    }
  }, [searchId, token])

  // Hook para mensajes en tiempo real
  const { reconnect: reconnectMessages } = useMessages({
    conversationId: state.conversation?.Id ?? null,
    onNewMessage: useCallback((message: MessageDto) => {
      // Enriquecer mensaje con SenderName si tenemos la info
      const enrichedMessage = {
        ...message,
        SenderName: message.SenderId === userId ? 'Tú' : message.SenderName
      }
      dispatch({ type: 'ADD_MESSAGE', payload: enrichedMessage })
    }, [userId]),
    onMessageUpdated: useCallback((message: MessageDto) => {
      dispatch({ type: 'UPDATE_MESSAGE', payload: message })
    }, []),
    enabled: !!state.conversation
  })

  // Hook para typing indicator
  const { typingUsers, startTyping, stopTyping } = useTypingIndicator({
    conversationId: state.conversation?.Id ?? null,
    currentUserId: userId,
    token,
    enabled: !!state.conversation
  })

  // Hook para presencia
  const { onlineUsers, isConnected } = usePresence({
    conversationId: state.conversation?.Id ?? null,
    userId,
    enabled: !!state.conversation
  })

  // Actualizar estado cuando cambian typing users u online users
  useEffect(() => {
    dispatch({ type: 'SET_TYPING_USERS', payload: typingUsers })
  }, [typingUsers])

  useEffect(() => {
    dispatch({ type: 'SET_ONLINE_USERS', payload: onlineUsers })
  }, [onlineUsers])

  useEffect(() => {
    dispatch({ type: 'SET_CONNECTED', payload: isConnected })
  }, [isConnected])

  // Enviar mensaje
  const send = useCallback(async (content: string, attachments?: File[]) => {
    if (!state.conversation || (!content.trim() && !attachments?.length)) {
      return
    }

    setIsSending(true)
    stopTyping()

    try {
      const dto: SendMessageDto = {
        ConversationId: state.conversation.Id,
        Content: content.trim() || undefined,
        Attachments: attachments
      }

      const newMessage = await sendMessage(dto, token)
      
      // El mensaje llegará por Postgres Changes, pero lo agregamos inmediatamente
      // para mejor UX (optimistic update)
      dispatch({ type: 'ADD_MESSAGE', payload: newMessage })
    } catch (error) {
      console.error('Error enviando mensaje:', error)
      throw error
    } finally {
      setIsSending(false)
    }
  }, [state.conversation, token, stopTyping])

  // Enviar ubicación
  const sendLocation = useCallback(async (latitude: number, longitude: number) => {
    if (!state.conversation) {
      return
    }

    setIsSending(true)

    try {
      const dto: SendMessageDto = {
        ConversationId: state.conversation.Id,
        LocationLatitude: latitude.toString(),
        LocationLongitude: longitude.toString()
      }

      const newMessage = await sendMessage(dto, token)
      dispatch({ type: 'ADD_MESSAGE', payload: newMessage })
    } catch (error) {
      console.error('Error enviando ubicación:', error)
      throw error
    } finally {
      setIsSending(false)
    }
  }, [state.conversation, token])

  // Marcar mensaje como leído
  const markAsRead = useCallback(async (messageId: number) => {
    try {
      await markMessageAsRead(messageId, token)
    } catch (error) {
      console.error('Error marcando mensaje como leído:', error)
    }
  }, [token])

  return {
    // Estado
    conversation: state.conversation,
    messages: state.messages,
    isLoading: state.isLoading,
    error: state.error,
    typingUsers: state.typingUsers,
    onlineUsers: state.onlineUsers,
    isConnected: state.isConnected,
    isSending,
    
    // Acciones
    send,
    sendLocation,
    markAsRead,
    startTyping,
    stopTyping,
    reconnect: reconnectMessages
  }
}
```

---

## 7. Componentes de Chat

### `src/components/chat/MessageInput.tsx`

```tsx
import React, { useState, useRef, useCallback, useEffect } from 'react'

interface MessageInputProps {
  onSend: (content: string, attachments?: File[]) => Promise<void>
  onTyping: () => void
  onStopTyping: () => void
  disabled?: boolean
  isSending?: boolean
}

export function MessageInput({
  onSend,
  onTyping,
  onStopTyping,
  disabled = false,
  isSending = false
}: MessageInputProps) {
  const [content, setContent] = useState('')
  const [attachments, setAttachments] = useState<File[]>([])
  const fileInputRef = useRef<HTMLInputElement>(null)
  const typingTimeoutRef = useRef<NodeJS.Timeout | null>(null)

  // Manejar cambio de texto con debounce para typing
  const handleChange = useCallback((e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const value = e.target.value
    setContent(value)

    // Notificar que estamos escribiendo
    onTyping()

    // Limpiar timeout anterior
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current)
    }

    // Dejar de "escribir" después de 2 segundos sin actividad
    typingTimeoutRef.current = setTimeout(() => {
      onStopTyping()
    }, 2000)
  }, [onTyping, onStopTyping])

  // Limpiar timeout al desmontar
  useEffect(() => {
    return () => {
      if (typingTimeoutRef.current) {
        clearTimeout(typingTimeoutRef.current)
      }
    }
  }, [])

  // Enviar mensaje
  const handleSend = async () => {
    if ((!content.trim() && attachments.length === 0) || isSending) {
      return
    }

    try {
      await onSend(content, attachments.length > 0 ? attachments : undefined)
      setContent('')
      setAttachments([])
      onStopTyping()
    } catch (error) {
      console.error('Error al enviar:', error)
    }
  }

  // Manejar Enter para enviar
  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSend()
    }
  }

  // Manejar archivos adjuntos
  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      const newFiles = Array.from(e.target.files)
      // Filtrar archivos válidos (imágenes y videos < 10MB)
      const validFiles = newFiles.filter(file => {
        const isValidType = file.type.startsWith('image/') || file.type === 'video/mp4'
        const isValidSize = file.size <= 10 * 1024 * 1024 // 10MB
        return isValidType && isValidSize
      })
      setAttachments(prev => [...prev, ...validFiles])
    }
  }

  const removeAttachment = (index: number) => {
    setAttachments(prev => prev.filter((_, i) => i !== index))
  }

  return (
    <div className="message-input-container">
      {/* Preview de archivos adjuntos */}
      {attachments.length > 0 && (
        <div className="attachments-preview">
          {attachments.map((file, index) => (
            <div key={index} className="attachment-item">
              <span>{file.name}</span>
              <button onClick={() => removeAttachment(index)}>×</button>
            </div>
          ))}
        </div>
      )}

      <div className="input-row">
        {/* Botón de adjuntar */}
        <button
          type="button"
          onClick={() => fileInputRef.current?.click()}
          disabled={disabled}
          className="attach-button"
          title="Adjuntar archivo"
        >
          📎
        </button>

        <input
          type="file"
          ref={fileInputRef}
          onChange={handleFileChange}
          accept="image/jpeg,image/png,video/mp4"
          multiple
          hidden
        />

        {/* Textarea */}
        <textarea
          value={content}
          onChange={handleChange}
          onKeyPress={handleKeyPress}
          placeholder="Escribe un mensaje..."
          disabled={disabled || isSending}
          rows={1}
          className="message-textarea"
        />

        {/* Botón de enviar */}
        <button
          onClick={handleSend}
          disabled={disabled || isSending || (!content.trim() && attachments.length === 0)}
          className="send-button"
        >
          {isSending ? '⏳' : '➤'}
        </button>
      </div>
    </div>
  )
}
```

### `src/components/chat/TypingIndicator.tsx`

```tsx
import React from 'react'

interface TypingIndicatorProps {
  typingUsers: number[]
  getUserName?: (userId: number) => string
}

export function TypingIndicator({ typingUsers, getUserName }: TypingIndicatorProps) {
  if (typingUsers.length === 0) {
    return null
  }

  const getTypingText = () => {
    if (typingUsers.length === 1) {
      const name = getUserName?.(typingUsers[0]) || 'Alguien'
      return `${name} está escribiendo...`
    }
    if (typingUsers.length === 2) {
      return '2 personas están escribiendo...'
    }
    return `${typingUsers.length} personas están escribiendo...`
  }

  return (
    <div className="typing-indicator">
      <div className="typing-dots">
        <span className="dot"></span>
        <span className="dot"></span>
        <span className="dot"></span>
      </div>
      <span className="typing-text">{getTypingText()}</span>
    </div>
  )
}
```

### `src/components/chat/MessageList.tsx`

```tsx
import React, { useEffect, useRef } from 'react'
import { MessageItem } from './MessageItem'
import type { MessageDto } from '../../types/chat.types'

interface MessageListProps {
  messages: MessageDto[]
  currentUserId: number
  onMessageVisible?: (messageId: number) => void
}

export function MessageList({ 
  messages, 
  currentUserId,
  onMessageVisible 
}: MessageListProps) {
  const containerRef = useRef<HTMLDivElement>(null)
  const bottomRef = useRef<HTMLDivElement>(null)

  // Auto-scroll al final cuando llegan nuevos mensajes
  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages.length])

  // Intersection Observer para marcar mensajes como leídos
  useEffect(() => {
    if (!onMessageVisible) return

    const observer = new IntersectionObserver(
      (entries) => {
        entries.forEach((entry) => {
          if (entry.isIntersecting) {
            const messageId = parseInt(entry.target.getAttribute('data-message-id') || '0')
            if (messageId) {
              onMessageVisible(messageId)
            }
          }
        })
      },
      { threshold: 0.5 }
    )

    const messageElements = containerRef.current?.querySelectorAll('[data-message-id]')
    messageElements?.forEach((el) => observer.observe(el))

    return () => observer.disconnect()
  }, [messages, onMessageVisible])

  return (
    <div className="message-list" ref={containerRef}>
      {messages.length === 0 ? (
        <div className="no-messages">
          <p>No hay mensajes aún</p>
          <p>¡Inicia la conversación!</p>
        </div>
      ) : (
        messages.map((message) => (
          <MessageItem
            key={message.Id}
            message={message}
            isOwn={message.SenderId === currentUserId}
          />
        ))
      )}
      <div ref={bottomRef} />
    </div>
  )
}
```

### `src/components/chat/MessageItem.tsx`

```tsx
import React from 'react'
import type { MessageDto } from '../../types/chat.types'

interface MessageItemProps {
  message: MessageDto
  isOwn: boolean
}

export function MessageItem({ message, isOwn }: MessageItemProps) {
  const formatTime = (dateString: string) => {
    const date = new Date(dateString)
    return date.toLocaleTimeString('es-ES', { 
      hour: '2-digit', 
      minute: '2-digit' 
    })
  }

  const hasLocation = message.LocationLatitude && message.LocationLongitude

  return (
    <div 
      className={`message-item ${isOwn ? 'own' : 'other'}`}
      data-message-id={message.Id}
    >
      {/* Nombre del remitente (solo para mensajes de otros) */}
      {!isOwn && message.SenderName && (
        <div className="sender-name">{message.SenderName}</div>
      )}

      {/* Contenido del mensaje */}
      {message.Content && (
        <div className="message-content">{message.Content}</div>
      )}

      {/* Ubicación */}
      {hasLocation && (
        <div className="message-location">
          <a
            href={`https://www.google.com/maps?q=${message.LocationLatitude},${message.LocationLongitude}`}
            target="_blank"
            rel="noopener noreferrer"
          >
            📍 Ver ubicación
          </a>
        </div>
      )}

      {/* Adjuntos */}
      {message.AttachmentUrls.length > 0 && (
        <div className="message-attachments">
          {message.AttachmentUrls.map((url, index) => {
            const isVideo = url.includes('.mp4')
            return isVideo ? (
              <video key={index} src={url} controls className="attachment-video" />
            ) : (
              <img key={index} src={url} alt="Adjunto" className="attachment-image" />
            )
          })}
        </div>
      )}

      {/* Hora y estado */}
      <div className="message-meta">
        <span className="message-time">{formatTime(message.SentAt)}</span>
        {isOwn && (
          <span className="message-status">
            {message.IsRead ? '✓✓' : '✓'}
          </span>
        )}
      </div>
    </div>
  )
}
```

### `src/components/chat/Chat.tsx` - Componente Principal

```tsx
import React from 'react'
import { useConversation } from '../../hooks/useConversation'
import { MessageList } from './MessageList'
import { MessageInput } from './MessageInput'
import { TypingIndicator } from './TypingIndicator'

interface ChatProps {
  searchId: number
  userId: number
  token: string
}

export function Chat({ searchId, userId, token }: ChatProps) {
  const {
    conversation,
    messages,
    isLoading,
    error,
    typingUsers,
    onlineUsers,
    isConnected,
    isSending,
    send,
    markAsRead,
    startTyping,
    stopTyping
  } = useConversation({ searchId, userId, token })

  // Estado de carga
  if (isLoading) {
    return (
      <div className="chat-container loading">
        <div className="spinner"></div>
        <p>Cargando conversación...</p>
      </div>
    )
  }

  // Error
  if (error) {
    return (
      <div className="chat-container error">
        <p>❌ Error: {error}</p>
        <button onClick={() => window.location.reload()}>
          Reintentar
        </button>
      </div>
    )
  }

  // Sin conversación
  if (!conversation) {
    return (
      <div className="chat-container empty">
        <p>No se encontró la conversación</p>
      </div>
    )
  }

  return (
    <div className="chat-container">
      {/* Header */}
      <div className="chat-header">
        <div className="connection-status">
          <span className={`status-dot ${isConnected ? 'connected' : 'disconnected'}`} />
          {isConnected ? 'Conectado' : 'Reconectando...'}
        </div>
        <div className="online-users">
          {onlineUsers.length} usuario(s) online
        </div>
      </div>

      {/* Lista de mensajes */}
      <MessageList
        messages={messages}
        currentUserId={userId}
        onMessageVisible={(messageId) => {
          const msg = messages.find(m => m.Id === messageId)
          if (msg && !msg.IsRead && msg.SenderId !== userId) {
            markAsRead(messageId)
          }
        }}
      />

      {/* Indicador de typing */}
      <TypingIndicator typingUsers={typingUsers} />

      {/* Input de mensaje */}
      <MessageInput
        onSend={send}
        onTyping={startTyping}
        onStopTyping={stopTyping}
        disabled={!isConnected}
        isSending={isSending}
      />
    </div>
  )
}
```

---

## 8. Integración Completa

### Ejemplo de uso en una página

```tsx
// pages/ChatPage.tsx
import React from 'react'
import { useParams } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth' // Tu hook de autenticación
import { Chat } from '../components/chat/Chat'

export function ChatPage() {
  const { searchId } = useParams<{ searchId: string }>()
  const { user, token } = useAuth()

  if (!user || !token) {
    return <div>Debes iniciar sesión para ver el chat</div>
  }

  if (!searchId) {
    return <div>ID de búsqueda no válido</div>
  }

  return (
    <div className="chat-page">
      <Chat
        searchId={parseInt(searchId)}
        userId={user.id}
        token={token}
      />
    </div>
  )
}
```

### CSS Base

```css
/* styles/chat.css */

.chat-container {
  display: flex;
  flex-direction: column;
  height: 100%;
  max-height: 600px;
  background: #f5f5f5;
  border-radius: 8px;
  overflow: hidden;
}

.chat-container.loading,
.chat-container.error,
.chat-container.empty {
  justify-content: center;
  align-items: center;
  padding: 2rem;
}

.chat-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem;
  background: #fff;
  border-bottom: 1px solid #e0e0e0;
}

.connection-status {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.875rem;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.status-dot.connected {
  background: #4caf50;
}

.status-dot.disconnected {
  background: #f44336;
}

.message-list {
  flex: 1;
  overflow-y: auto;
  padding: 1rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.message-item {
  max-width: 70%;
  padding: 0.75rem 1rem;
  border-radius: 12px;
  word-wrap: break-word;
}

.message-item.own {
  align-self: flex-end;
  background: #007bff;
  color: white;
  border-bottom-right-radius: 4px;
}

.message-item.other {
  align-self: flex-start;
  background: white;
  border-bottom-left-radius: 4px;
}

.sender-name {
  font-size: 0.75rem;
  font-weight: 600;
  margin-bottom: 0.25rem;
  color: #666;
}

.message-content {
  line-height: 1.4;
}

.message-meta {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.25rem;
  font-size: 0.7rem;
  opacity: 0.7;
}

.message-attachments {
  margin-top: 0.5rem;
}

.attachment-image {
  max-width: 100%;
  max-height: 200px;
  border-radius: 8px;
}

.attachment-video {
  max-width: 100%;
  max-height: 200px;
  border-radius: 8px;
}

.typing-indicator {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  font-size: 0.875rem;
  color: #666;
}

.typing-dots {
  display: flex;
  gap: 3px;
}

.typing-dots .dot {
  width: 6px;
  height: 6px;
  background: #999;
  border-radius: 50%;
  animation: typing 1.4s infinite;
}

.typing-dots .dot:nth-child(2) {
  animation-delay: 0.2s;
}

.typing-dots .dot:nth-child(3) {
  animation-delay: 0.4s;
}

@keyframes typing {
  0%, 60%, 100% {
    transform: translateY(0);
  }
  30% {
    transform: translateY(-4px);
  }
}

.message-input-container {
  padding: 1rem;
  background: white;
  border-top: 1px solid #e0e0e0;
}

.attachments-preview {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.attachment-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.25rem 0.5rem;
  background: #e3f2fd;
  border-radius: 4px;
  font-size: 0.875rem;
}

.attachment-item button {
  background: none;
  border: none;
  cursor: pointer;
  font-size: 1rem;
}

.input-row {
  display: flex;
  gap: 0.5rem;
  align-items: flex-end;
}

.attach-button,
.send-button {
  width: 40px;
  height: 40px;
  border: none;
  border-radius: 50%;
  cursor: pointer;
  font-size: 1.25rem;
  display: flex;
  align-items: center;
  justify-content: center;
}

.attach-button {
  background: #f0f0f0;
}

.send-button {
  background: #007bff;
  color: white;
}

.send-button:disabled {
  background: #ccc;
  cursor: not-allowed;
}

.message-textarea {
  flex: 1;
  padding: 0.75rem;
  border: 1px solid #e0e0e0;
  border-radius: 20px;
  resize: none;
  font-family: inherit;
  font-size: 1rem;
  max-height: 120px;
}

.message-textarea:focus {
  outline: none;
  border-color: #007bff;
}

.no-messages {
  text-align: center;
  color: #999;
  padding: 2rem;
}
```

---

## 9. Manejo de Errores y Reconexión

```typescript
// hooks/useRealtimeStatus.ts
import { useEffect, useState } from 'react'
import { supabase } from '../lib/supabase'

export function useRealtimeStatus() {
  const [status, setStatus] = useState<'connecting' | 'connected' | 'disconnected'>('connecting')

  useEffect(() => {
    // Escuchar cambios en el estado de la conexión
    const handleOnline = () => {
      console.log('🌐 Conexión a internet restaurada')
      // Forzar reconexión de todos los canales
      supabase.realtime.connect()
    }

    const handleOffline = () => {
      console.log('📵 Sin conexión a internet')
      setStatus('disconnected')
    }

    window.addEventListener('online', handleOnline)
    window.addEventListener('offline', handleOffline)

    // Verificar estado inicial
    if (navigator.onLine) {
      setStatus('connected')
    } else {
      setStatus('disconnected')
    }

    return () => {
      window.removeEventListener('online', handleOnline)
      window.removeEventListener('offline', handleOffline)
    }
  }, [])

  return status
}
```

---

## 10. Testing y Debugging

### Verificar en la consola del navegador

```javascript
// Verificar conexión a Supabase
const { createClient } = await import('https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2/+esm')

const supabase = createClient(
  'https://rveqsehzlvbttlpmsbmi.supabase.co',
  'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'
)

// Probar suscripción a mensajes
const channel = supabase
  .channel('test-messages')
  .on('postgres_changes', {
    event: 'INSERT',
    schema: 'public',
    table: 'Messages'
  }, (payload) => {
    console.log('Nuevo mensaje:', payload)
  })
  .subscribe((status) => {
    console.log('Estado:', status)
  })

// Para desuscribirse
// supabase.removeChannel(channel)
```

### Logs útiles para debugging

```typescript
// En tus hooks, añade logs detallados
console.log('🔌 Conectando a canal:', channelName)
console.log('📡 Estado de suscripción:', status)
console.log('📩 Mensaje recibido:', payload)
console.log('❌ Error:', error)
```

---

## 📞 Soporte

Si tienes problemas:

1. Verifica las credenciales de Supabase en las variables de entorno
2. Asegúrate de que las tablas `Messages` y `Conversations` tienen Realtime habilitado
3. Comprueba que tienes conexión a internet
4. Revisa la consola del navegador para errores

**Endpoints de la API:**
- `GET /api/Chat/conversation?searchId={id}` - Obtener/crear conversación
- `GET /api/Chat/by-searchhire/{id}` - Obtener por SearchHireId  
- `POST /api/Chat/message` - Enviar mensaje (FormData)
- `PUT /api/Chat/message/{id}/read` - Marcar como leído
- `POST /api/Chat/typing` - Notificar typing

**URL de Supabase:** `https://rveqsehzlvbttlpmsbmi.supabase.co`

**Keys:**
- Publishable: `sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0`
- Anon (legacy): `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InJ2ZXFzZWh6bHZidHRscG1zYm1pIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Njc0NDkyMTcsImV4cCI6MjA4MzAyNTIxN30.LA_zA1QezNnVU2dsojD6adI01V3ZN3uUNU1rB78DqF8`
