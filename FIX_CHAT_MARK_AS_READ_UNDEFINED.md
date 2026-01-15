# Fix: Error "messageId undefined" al marcar mensajes como leídos

## 🔴 Problema

El frontend está intentando marcar mensajes como leídos, pero el `messageId` es `undefined`:

```
API Error: PUT http://localhost:7124/api/chat/message/undefined/read
Status: 400 Bad Request
Error: "The value 'undefined' is not valid." for messageId
```

## 🔍 Causa

El problema ocurre cuando:
1. **Mensajes locales sin ID**: Mensajes que se están enviando pero aún no tienen un `Id` del backend
2. **Mensajes de Supabase Realtime**: Los mensajes que vienen de `postgres_changes` pueden tener el campo `Id` con diferente formato (mayúscula/minúscula)
3. **Mensajes sin validar**: El código intenta marcar mensajes sin verificar que tengan un `Id` válido

## ✅ Solución

### 1. Verificar que el mensaje tenga ID antes de marcarlo como leído

En el hook `useChat.ts` o donde se marcan los mensajes como leídos:

```typescript
// ❌ ANTES (causa el error)
const markMessageAsRead = async (message: MessageDto) => {
  await fetch(`/api/chat/message/${message.Id}/read`, {
    method: 'PUT',
    // ...
  })
}

// ✅ DESPUÉS (con validación)
const markMessageAsRead = async (message: MessageDto) => {
  // Verificar que el mensaje tenga un ID válido
  const messageId = message.Id || message.id
  
  if (!messageId || messageId === undefined || messageId === null) {
    console.warn('[Chat] Cannot mark message as read: message has no ID', message)
    return
  }
  
  // Verificar que no sea un mensaje propio (el backend rechaza esto)
  if (message.SenderId === userId || message.senderId === userId) {
    return // No marcar mensajes propios como leídos
  }
  
  // Verificar que no esté ya marcado como leído
  if (message.IsRead || message.isRead) {
    return // Ya está marcado como leído
  }
  
  try {
    await fetch(`/api/chat/message/${messageId}/read`, {
      method: 'PUT',
      headers: {
        'Authorization': `Bearer ${token}`
      }
    })
  } catch (error) {
    console.error('[Chat] Failed to mark message as read:', error)
  }
}
```

### 2. Normalizar los campos del mensaje

Cuando recibes mensajes de Supabase Realtime, normaliza los campos:

```typescript
// Normalizar mensaje de Supabase Realtime
const normalizeMessage = (message: any): MessageDto => {
  return {
    Id: message.Id || message.id || message.Id,
    ConversationId: message.ConversationId || message.conversationId || message.ConversationId,
    SenderId: message.SenderId ?? message.senderId ?? null,
    Content: message.Content ?? message.content ?? null,
    SentAt: message.SentAt || message.sentAt || message.SentAt,
    IsRead: message.IsRead ?? message.isRead ?? false,
    SenderName: message.SenderName || message.senderName || null,
    LocationLatitude: message.LocationLatitude || message.locationLatitude || null,
    LocationLongitude: message.LocationLongitude || message.locationLongitude || null,
    AttachmentUrls: message.AttachmentUrls || message.attachmentUrls || []
  }
}

// Al recibir mensaje de Supabase
const channel = supabase
  .channel(`messages:conversation:${conversationId}`)
  .on('postgres_changes', {
    event: 'INSERT',
    schema: 'public',
    table: 'Messages',
    filter: `ConversationId=eq.${conversationId}`
  }, (payload) => {
    const normalizedMessage = normalizeMessage(payload.new)
    // Ahora puedes usar normalizedMessage.Id con seguridad
    onNewMessage(normalizedMessage)
  })
  .subscribe()
```

### 3. Filtrar mensajes sin ID al marcar como leídos

Al marcar mensajes no leídos, filtra los que no tienen ID:

```typescript
// Marcar mensajes no leídos como leídos
const markUnreadMessagesAsRead = async (messages: MessageDto[], userId: number) => {
  // Filtrar solo mensajes que:
  // 1. Tienen un ID válido
  // 2. No son del usuario actual
  // 3. No están ya marcados como leídos
  const unreadMessages = messages.filter(msg => {
    const messageId = msg.Id || msg.id
    const senderId = msg.SenderId ?? msg.senderId
    
    return (
      messageId && 
      messageId !== undefined && 
      messageId !== null &&
      senderId !== userId &&
      !(msg.IsRead || msg.isRead)
    )
  })
  
  // Marcar cada mensaje como leído
  for (const message of unreadMessages) {
    const messageId = message.Id || message.id
    if (messageId) {
      await markMessageAsRead(message)
    }
  }
}
```

### 4. Manejar errores correctamente

```typescript
const markMessageAsRead = async (message: MessageDto) => {
  const messageId = message.Id || message.id
  
  if (!messageId) {
    console.warn('[Chat] Skipping mark as read: message has no ID', {
      message,
      hasId: !!message.Id,
      hasIdLowercase: !!message.id
    })
    return
  }
  
  try {
    const response = await fetch(`/api/chat/message/${messageId}/read`, {
      method: 'PUT',
      headers: {
        'Authorization': `Bearer ${token}`,
        'Content-Type': 'application/json'
      }
    })
    
    if (!response.ok) {
      // Si es 400, probablemente es porque el mensaje es propio o ya está leído
      if (response.status === 400) {
        const error = await response.json()
        console.warn('[Chat] Cannot mark message as read:', error.message || error)
        return
      }
      
      throw new Error(`Failed to mark message as read: ${response.status}`)
    }
    
    // Actualizar estado local
    updateMessageReadStatus(messageId, true)
  } catch (error) {
    console.error('[Chat] Error marking message as read:', error)
    // No lanzar error, solo loggear - el mensaje se marcará en la próxima carga
  }
}
```

## 📋 Checklist de Implementación

- [ ] Agregar validación de `Id` antes de marcar como leído
- [ ] Normalizar campos de mensajes de Supabase Realtime
- [ ] Filtrar mensajes sin ID al marcar como leídos
- [ ] Manejar errores 400 (mensaje propio o ya leído)
- [ ] Agregar logs para debugging
- [ ] Verificar que solo se marquen mensajes de otros usuarios

## 🔍 Debugging

Para debuggear el problema, agrega estos logs:

```typescript
console.log('[Chat] Marking message as read:', {
  messageId: message.Id || message.id,
  hasId: !!message.Id,
  hasIdLowercase: !!message.id,
  senderId: message.SenderId || message.senderId,
  isRead: message.IsRead || message.isRead,
  fullMessage: message
})
```

## 📝 Notas Importantes

1. **El backend rechaza marcar mensajes propios como leídos** - Esto es intencional
2. **Los mensajes locales** (que aún no se han guardado) no tienen `Id` - No intentes marcarlos
3. **Supabase Realtime** puede enviar campos con diferentes formatos - Normaliza siempre
4. **Solo marca como leídos mensajes de otros usuarios** - El backend valida esto

## 🎯 Ejemplo Completo

```typescript
// useChat.ts
export function useChat(conversationId: number, userId: number, token: string) {
  const [messages, setMessages] = useState<MessageDto[]>([])
  
  // Normalizar mensaje
  const normalizeMessage = (msg: any): MessageDto => ({
    Id: msg.Id || msg.id,
    ConversationId: msg.ConversationId || msg.conversationId,
    SenderId: msg.SenderId ?? msg.senderId ?? null,
    Content: msg.Content ?? msg.content ?? null,
    SentAt: msg.SentAt || msg.sentAt,
    IsRead: msg.IsRead ?? msg.isRead ?? false,
    SenderName: msg.SenderName || msg.senderName || null,
    LocationLatitude: msg.LocationLatitude || msg.locationLatitude || null,
    LocationLongitude: msg.LocationLongitude || msg.locationLongitude || null,
    AttachmentUrls: msg.AttachmentUrls || msg.attachmentUrls || []
  })
  
  // Marcar mensaje como leído
  const markAsRead = useCallback(async (message: MessageDto) => {
    const messageId = message.Id || message.id
    
    // Validaciones
    if (!messageId) {
      console.warn('[Chat] Message has no ID, skipping mark as read')
      return
    }
    
    if (message.SenderId === userId || message.senderId === userId) {
      return // No marcar mensajes propios
    }
    
    if (message.IsRead || message.isRead) {
      return // Ya está leído
    }
    
    try {
      const response = await fetch(
        `${API_URL}/api/chat/message/${messageId}/read`,
        {
          method: 'PUT',
          headers: {
            'Authorization': `Bearer ${token}`
          }
        }
      )
      
      if (!response.ok) {
        if (response.status === 400) {
          const error = await response.json()
          console.warn('[Chat] Cannot mark as read:', error.message)
          return
        }
        throw new Error(`HTTP ${response.status}`)
      }
      
      // Actualizar estado local
      setMessages(prev => 
        prev.map(m => {
          const mId = m.Id || m.id
          return mId === messageId ? { ...m, IsRead: true } : m
        })
      )
    } catch (error) {
      console.error('[Chat] Error marking as read:', error)
    }
  }, [userId, token])
  
  // Marcar mensajes no leídos cuando se cargan
  useEffect(() => {
    const unreadMessages = messages.filter(msg => {
      const msgId = msg.Id || msg.id
      const senderId = msg.SenderId ?? msg.senderId
      const isRead = msg.IsRead ?? msg.isRead
      
      return msgId && senderId !== userId && !isRead
    })
    
    unreadMessages.forEach(msg => markAsRead(msg))
  }, [messages, userId, markAsRead])
  
  // Escuchar nuevos mensajes de Supabase
  useEffect(() => {
    const channel = supabase
      .channel(`messages:conversation:${conversationId}`)
      .on('postgres_changes', {
        event: 'INSERT',
        schema: 'public',
        table: 'Messages',
        filter: `ConversationId=eq.${conversationId}`
      }, (payload) => {
        const normalized = normalizeMessage(payload.new)
        setMessages(prev => [...prev, normalized])
        
        // Marcar como leído si no es nuestro mensaje
        if (normalized.SenderId !== userId && normalized.senderId !== userId) {
          markAsRead(normalized)
        }
      })
      .subscribe()
    
    return () => {
      supabase.removeChannel(channel)
    }
  }, [conversationId, userId, markAsRead])
  
  return { messages, markAsRead }
}
```

---

**Fecha:** 15 de enero de 2026  
**Problema:** `messageId undefined` al marcar mensajes como leídos  
**Solución:** Validar `Id` antes de marcar, normalizar campos de Supabase Realtime
