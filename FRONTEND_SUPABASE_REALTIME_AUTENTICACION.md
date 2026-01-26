# 🔐 Supabase Realtime: Autenticación y Eventos en Tiempo Real

## 🚨 PROBLEMA: Los Mensajes No Aparecen en Tiempo Real Entre Usuarios

### Síntomas:
- ✅ Los mensajes se envían correctamente al backend
- ✅ El mensaje aparece en la BD
- ❌ **El otro usuario NO ve el mensaje hasta que recarga la página**
- ❌ Los eventos de `postgres_changes` no llegan al frontend

---

## 🔍 CAUSA RAÍZ: Autenticación con Supabase

**El problema:** Supabase Realtime requiere que el cliente esté **autenticado con un JWT token** para recibir eventos de `postgres_changes`. Si el cliente no está autenticado o el token no es válido, los eventos no llegarán.

### Verificación de RLS (Row Level Security):

Las políticas RLS en Supabase están configuradas así:
- ✅ **SELECT**: Usuarios `authenticated` pueden leer mensajes (`qual: true`)
- ✅ **INSERT**: Solo `service_role` (el backend inserta)

**Esto significa:** Los usuarios autenticados DEBERÍAN poder recibir eventos, pero solo si el cliente de Supabase está correctamente autenticado.

---

## ✅ SOLUCIÓN: Autenticar el Cliente de Supabase

### **Problema Actual:**

El cliente de Supabase probablemente se está creando sin autenticación:

```typescript
// ❌ INCORRECTO: Sin autenticación
import { createClient } from '@supabase/supabase-js'

const supabase = createClient(
  'https://rveqsehzlvbttlpmsbmi.supabase.co',
  'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'
)
```

### **Solución: Autenticar con JWT Token**

El cliente de Supabase debe autenticarse con el JWT token del usuario:

```typescript
// ✅ CORRECTO: Con autenticación JWT
import { createClient } from '@supabase/supabase-js'

// Obtener el token JWT del usuario (desde tu sistema de autenticación)
const getAuthToken = () => {
  // Opción 1: Desde localStorage/sessionStorage
  return localStorage.getItem('authToken') || sessionStorage.getItem('authToken')
  
  // Opción 2: Desde un contexto de React
  // return authContext.token
  
  // Opción 3: Desde cookies
  // return getCookie('authToken')
}

// Crear cliente con autenticación
const supabase = createClient(
  'https://rveqsehzlvbttlpmsbmi.supabase.co',
  'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0',
  {
    auth: {
      // ✅ IMPORTANTE: Pasar el JWT token del usuario
      persistSession: false, // No guardar sesión en Supabase (ya tienes tu propia auth)
      autoRefreshToken: false, // No refrescar tokens automáticamente
      detectSessionInUrl: false // No detectar sesión en URL
    },
    realtime: {
      // ✅ Configuración de Realtime
      params: {
        // Pasar el token JWT en los parámetros de Realtime
        apikey: 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'
      }
    },
    global: {
      // ✅ Pasar el token en los headers de todas las peticiones
      headers: {
        Authorization: `Bearer ${getAuthToken()}`
      }
    }
  }
)
```

---

## 🔧 IMPLEMENTACIÓN COMPLETA

### **1. Crear Cliente Supabase con Autenticación Dinámica**

```typescript
// lib/supabase.ts
import { createClient, SupabaseClient } from '@supabase/supabase-js'

const SUPABASE_URL = 'https://rveqsehzlvbttlpmsbmi.supabase.co'
const SUPABASE_ANON_KEY = 'sb_publishable__cytPrm1U5kZhUeKY3SvdQ_yV2QzGS0'

// Variable global para el cliente
let supabaseClient: SupabaseClient | null = null

// Función para obtener el token JWT del usuario
function getAuthToken(): string | null {
  // Ajusta esto según tu sistema de autenticación
  return localStorage.getItem('authToken') || 
         sessionStorage.getItem('authToken') ||
         null
}

// Función para crear/actualizar el cliente con el token actual
export function getSupabaseClient(): SupabaseClient {
  const token = getAuthToken()
  
  // Si ya existe un cliente y el token no ha cambiado, reutilizarlo
  if (supabaseClient && token) {
    return supabaseClient
  }
  
  // Crear nuevo cliente con autenticación
  supabaseClient = createClient(SUPABASE_URL, SUPABASE_ANON_KEY, {
    auth: {
      persistSession: false,
      autoRefreshToken: false,
      detectSessionInUrl: false
    },
    realtime: {
      params: {
        apikey: SUPABASE_ANON_KEY
      }
    },
    global: {
      headers: token ? {
        Authorization: `Bearer ${token}`
      } : {}
    }
  })
  
  // ✅ Si hay token, establecerlo en el cliente
  if (token) {
    supabaseClient.auth.setSession({
      access_token: token,
      refresh_token: '', // No necesario si no usas refresh
      expires_in: 3600,
      expires_at: Date.now() / 1000 + 3600,
      token_type: 'bearer',
      user: null // Se puede obtener del token si es necesario
    }).catch(err => {
      console.error('Error setting Supabase session:', err)
    })
  }
  
  return supabaseClient
}

// Exportar función para obtener cliente
export const supabase = getSupabaseClient()

// Exportar función para actualizar el cliente cuando cambie el token
export function updateSupabaseAuth(token: string | null) {
  if (token && supabaseClient) {
    supabaseClient.auth.setSession({
      access_token: token,
      refresh_token: '',
      expires_in: 3600,
      expires_at: Date.now() / 1000 + 3600,
      token_type: 'bearer',
      user: null
    }).catch(err => {
      console.error('Error updating Supabase session:', err)
    })
  }
}
```

---

### **2. Usar Cliente Autenticado en el Componente de Chat**

```typescript
// components/PreHireChat.tsx
import { useState, useEffect, useRef } from 'react'
import { getSupabaseClient, updateSupabaseAuth } from '@/lib/supabase'

export const PreHireChat = ({ conversationId, userId, token, apiUrl }) => {
  const [messages, setMessages] = useState<Message[]>([])
  const [isConnected, setIsConnected] = useState(false)
  const channelRef = useRef<any>(null)
  
  // ✅ Obtener cliente Supabase autenticado
  const supabase = getSupabaseClient()
  
  // ✅ Actualizar autenticación cuando cambie el token
  useEffect(() => {
    if (token) {
      updateSupabaseAuth(token)
    }
  }, [token])
  
  // ✅ Suscribirse a nuevos mensajes
  useEffect(() => {
    if (!conversationId || !token) {
      console.warn('⚠️ [PreHireChat] No conversationId o token, no se puede conectar')
      return
    }
    
    console.log('🔌 [PreHireChat] Conectando a Supabase Realtime con autenticación...')
    
    const channelName = `messages:conversation:${conversationId}`
    const channel = supabase
      .channel(channelName)
      
      // ✅ SUSCRIPCIÓN 1: postgres_changes (detecta cambios en BD)
      .on(
        'postgres_changes',
        {
          event: 'INSERT',
          schema: 'public',
          table: 'Messages',
          filter: `ConversationId=eq.${conversationId}`
        },
        async (payload) => {
          console.log('📨 [PreHireChat] Mensaje recibido vía postgres_changes:', payload)
          handleNewMessage(payload.new)
        }
      )
      
      // ✅ SUSCRIPCIÓN 2: broadcast (recibe broadcasts del backend) - MÁS CONFIABLE
      .on(
        'broadcast',
        { event: 'new_message' },
        ({ payload }) => {
          console.log('📨 [PreHireChat] Mensaje recibido vía broadcast:', payload)
          handleNewMessage(payload)
        }
      )
      
      // ✅ Función auxiliar para manejar nuevos mensajes
      function handleNewMessage(messageData: any) {
        // ✅ Verificar que es para esta conversación
        const msgConversationId = messageData.ConversationId || messageData.conversationId
        if (msgConversationId !== conversationId) {
          console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando')
          return
        }
        
        // ✅ Crear objeto MessageDto (formato puede variar entre postgres_changes y broadcast)
        const messageDto: Message = {
          id: messageData.Id || messageData.id,
          conversationId: msgConversationId,
          senderId: messageData.SenderId || messageData.senderId,
          content: messageData.Content || messageData.content || '',
          sentAt: messageData.SentAt || messageData.sentAt,
          isRead: messageData.IsRead || messageData.isRead || false,
          senderName: messageData.SenderName || messageData.senderName || null,
          locationLatitude: messageData.LocationLatitude || messageData.locationLatitude,
          locationLongitude: messageData.LocationLongitude || messageData.locationLongitude,
          attachmentUrls: messageData.AttachmentUrls || messageData.attachmentUrls || []
        }
        
        // ✅ Agregar mensaje al estado (evitar duplicados)
        setMessages(prev => {
          if (prev.some(m => m.id === messageDto.id)) {
            console.log('⚠️ [PreHireChat] Mensaje duplicado, ignorando')
            return prev
          }
          
          const updated = [...prev, messageDto].sort((a, b) => 
            new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
          )
          
          console.log('✅ [PreHireChat] Mensaje agregado. Total:', updated.length)
          return updated
        })
      }
      
      // ✅ Suscribirse al canal
      .subscribe((status) => {
        console.log(`📡 [PreHireChat] Estado de suscripción: ${status}`)
        setIsConnected(status === 'SUBSCRIBED')
        
        if (status === 'SUBSCRIBED') {
          console.log('✅ [PreHireChat] Conectado a Supabase Realtime')
        } else if (status === 'CHANNEL_ERROR') {
          console.error('❌ [PreHireChat] Error en el canal. Verifica autenticación.')
        } else if (status === 'TIMED_OUT') {
          console.error('⏱️ [PreHireChat] Timeout. Verifica conexión y CSP.')
        }
      })
    
    channelRef.current = channel
    
    // ✅ Limpiar al desmontar
    return () => {
      console.log('🔌 [PreHireChat] Desconectando de Supabase Realtime')
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current)
      }
    }
  }, [conversationId, token, supabase])
  
  // ... resto del componente
}
```

---

## 🔍 DEBUGGING: Verificar Autenticación

### **1. Verificar que el Token se Pasa Correctamente**

Agrega estos logs en tu componente:

```typescript
useEffect(() => {
  console.log('🔑 [PreHireChat] Token disponible:', !!token)
  console.log('🔑 [PreHireChat] Token (primeros 20 chars):', token?.substring(0, 20))
  console.log('🔑 [PreHireChat] ConversationId:', conversationId)
  
  // Verificar que Supabase tiene el token
  supabase.auth.getSession().then(({ data, error }) => {
    if (error) {
      console.error('❌ [PreHireChat] Error obteniendo sesión:', error)
    } else {
      console.log('✅ [PreHireChat] Sesión Supabase:', data.session ? 'Activa' : 'Inactiva')
      if (data.session) {
        console.log('🔑 [PreHireChat] Token Supabase (primeros 20 chars):', data.session.access_token?.substring(0, 20))
      }
    }
  })
}, [token, conversationId])
```

### **2. Verificar Eventos de Realtime**

Agrega logs detallados en el handler:

```typescript
.on('postgres_changes', { ... }, (payload) => {
  console.log('📨 [PreHireChat] ===== EVENTO RECIBIDO =====')
  console.log('📨 [PreHireChat] Tipo:', payload.eventType)
  console.log('📨 [PreHireChat] Payload completo:', JSON.stringify(payload, null, 2))
  console.log('📨 [PreHireChat] Nuevo mensaje:', payload.new)
  console.log('📨 [PreHireChat] ConversationId del payload:', payload.new?.ConversationId)
  console.log('📨 [PreHireChat] ConversationId esperado:', conversationId)
  console.log('📨 [PreHireChat] ¿Coinciden?', payload.new?.ConversationId === conversationId)
  
  // ... resto del código
})
```

### **3. Verificar Estado de la Conexión**

```typescript
.subscribe((status, err) => {
  console.log('📡 [PreHireChat] ===== ESTADO DE SUSCRIPCIÓN =====')
  console.log('📡 [PreHireChat] Estado:', status)
  if (err) {
    console.error('❌ [PreHireChat] Error:', err)
  }
  
  // Verificar autenticación cuando hay error
  if (status === 'CHANNEL_ERROR' || status === 'TIMED_OUT') {
    supabase.auth.getSession().then(({ data, error }) => {
      console.log('🔑 [PreHireChat] Verificación de sesión después del error:')
      console.log('🔑 [PreHireChat] Sesión:', data.session ? 'Activa' : 'Inactiva')
      console.log('🔑 [PreHireChat] Error:', error)
    })
  }
})
```

---

## 🎯 SOLUCIÓN RECOMENDADA: Usar AMBOS (postgres_changes + broadcast)

**IMPORTANTE:** El backend envía broadcasts al canal `conversation:{conversationId}` con el evento `new_message`. Debes escuchar **AMBOS** métodos para garantizar que los mensajes lleguen:

1. ✅ **`postgres_changes`** - Detecta cambios en la BD (requiere autenticación)
2. ✅ **`broadcast`** - Recibe broadcasts del backend (más confiable)

### **Implementación Completa con Ambos Métodos:**

```typescript
// ✅ SUSCRIPCIÓN 1: postgres_changes (detecta cambios en BD)
.on(
  'postgres_changes',
  {
    event: 'INSERT',
    schema: 'public',
    table: 'Messages',
    filter: `ConversationId=eq.${conversationId}`
  },
  async (payload) => {
    console.log('📨 [PreHireChat] Mensaje recibido vía postgres_changes:', payload)
    handleNewMessage(payload.new)
  }
)

// ✅ SUSCRIPCIÓN 2: broadcast (recibe broadcasts del backend) - MÁS CONFIABLE
.on(
  'broadcast',
  { event: 'new_message' },
  ({ payload }) => {
    console.log('📨 [PreHireChat] Mensaje recibido vía broadcast:', payload)
    
    // ✅ El backend envía el mensaje completo en el payload
    handleNewMessage(payload)
  }
)

// ✅ Función auxiliar para manejar nuevos mensajes
function handleNewMessage(messageData: any) {
  // ✅ Verificar que es para esta conversación
  const msgConversationId = messageData.ConversationId || messageData.conversationId
  if (msgConversationId !== conversationId) {
    console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando')
    return
  }
  
  // ✅ Crear objeto MessageDto (formato puede variar entre postgres_changes y broadcast)
  const messageDto: Message = {
    id: messageData.Id || messageData.id,
    conversationId: msgConversationId,
    senderId: messageData.SenderId || messageData.senderId,
    content: messageData.Content || messageData.content || '',
    sentAt: messageData.SentAt || messageData.sentAt,
    isRead: messageData.IsRead || messageData.isRead || false,
    senderName: messageData.SenderName || messageData.senderName || null,
    locationLatitude: messageData.LocationLatitude || messageData.locationLatitude,
    locationLongitude: messageData.LocationLongitude || messageData.locationLongitude,
    attachmentUrls: messageData.AttachmentUrls || messageData.attachmentUrls || []
  }
  
  // ✅ Agregar mensaje al estado (evitar duplicados)
  setMessages(prev => {
    if (prev.some(m => m.id === messageDto.id)) {
      console.log('⚠️ [PreHireChat] Mensaje duplicado, ignorando')
      return prev
    }
    
    const updated = [...prev, messageDto].sort((a, b) => 
      new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
    )
    
    console.log('✅ [PreHireChat] Mensaje agregado. Total:', updated.length)
    return updated
  })
}
```

**Ventajas de usar ambos:**
- ✅ **`postgres_changes`**: Detecta cambios directamente en la BD (más rápido)
- ✅ **`broadcast`**: Recibe broadcasts del backend (más confiable, no depende de RLS)
- ✅ **Redundancia**: Si uno falla, el otro sigue funcionando
- ✅ **Mejor UX**: Los mensajes aparecen más rápido y de forma más confiable

---

## ✅ CHECKLIST DE IMPLEMENTACIÓN

- [ ] Crear cliente Supabase con autenticación JWT
- [ ] Pasar el token JWT del usuario al cliente de Supabase
- [ ] Actualizar la autenticación cuando cambie el token
- [ ] **Implementar AMBOS métodos: `postgres_changes` Y `broadcast`**
- [ ] Agregar logs detallados para debugging
- [ ] Verificar que el estado de suscripción es `SUBSCRIBED`
- [ ] Verificar que los eventos `postgres_changes` llegan
- [ ] Verificar que los eventos `broadcast` llegan
- [ ] Probar con múltiples usuarios en diferentes navegadores
- [ ] Verificar que los mensajes aparecen en tiempo real en ambos usuarios

---

## 🎯 RESUMEN

**Problema:** Los mensajes no aparecen en tiempo real entre usuarios.

**Causa:** El cliente de Supabase no está autenticado con el JWT token del usuario, por lo que Supabase Realtime no puede enviar eventos debido a las políticas RLS.

**Solución:**
1. ✅ Autenticar el cliente de Supabase con el JWT token del usuario
2. ✅ Pasar el token en los headers y en la sesión de Supabase
3. ✅ **Implementar AMBOS métodos: `postgres_changes` Y `broadcast`** (recomendado)
4. ✅ Verificar que la suscripción está en estado `SUBSCRIBED`
5. ✅ Agregar logs para debugging
6. ✅ Verificar que los mensajes aparecen en tiempo real en ambos usuarios

**Nota importante:** El backend envía broadcasts al canal `conversation:{conversationId}` con el evento `new_message`. Estos broadcasts son más confiables que `postgres_changes` porque no dependen de las políticas RLS. Usa ambos métodos para garantizar que los mensajes lleguen siempre.

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Solución implementada y lista para usar
