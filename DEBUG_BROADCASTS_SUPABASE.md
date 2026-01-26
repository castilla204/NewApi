# 🔍 Debug: Broadcasts de Supabase Realtime No Llegan

## 📋 Análisis del Backend

El backend **SÍ está enviando broadcasts** correctamente:

### **Código del Backend:**

```csharp
// Services/SupabaseRealtimeService.cs línea 114-118
public async Task NotifyNewMessageAsync(int conversationId, object messageData)
{
    var channel = $"conversation:{conversationId}";
    await BroadcastToChannelAsync(channel, "new_message", messageData);
}

// BroadcastToChannelAsync envía a:
// POST https://rveqsehzlvbttlpmsbmi.supabase.co/realtime/v1/api/broadcast
// Body: {
//   "messages": [{
//     "topic": "conversation:61",
//     "event": "new_message",
//     "payload": { ... messageDto ... }
//   }]
// }
```

### **El MessageDto que se envía tiene propiedades en PascalCase:**

```csharp
var messageDto = new MessageDto
{
    Id = message.Id,                    // ✅ PascalCase
    ConversationId = message.ConversationId,  // ✅ PascalCase
    SenderId = message.SenderId,        // ✅ PascalCase
    Content = message.Content,          // ✅ PascalCase
    SentAt = message.SentAt,           // ✅ PascalCase
    IsRead = message.IsRead,           // ✅ PascalCase
    SenderName = senderName,            // ✅ PascalCase
    LocationLatitude = message.LocationLatitude,  // ✅ PascalCase
    LocationLongitude = message.LocationLongitude, // ✅ PascalCase
    AttachmentUrls = attachmentUrls    // ✅ PascalCase
};
```

---

## 🔍 PROBLEMA POTENCIAL: Formato del Payload

El frontend puede estar esperando propiedades en **camelCase**, pero el backend envía en **PascalCase**.

### **Solución: Verificar y Ajustar el Frontend**

El frontend debe leer las propiedades en **PascalCase** (como las envía el backend):

```typescript
// ✅ CORRECTO: Leer propiedades en PascalCase
.on(
  'broadcast',
  { event: 'new_message' },
  ({ payload }) => {
    console.log('📨 [PreHireChat] ===== EVENTO RECIBIDO (broadcast) =====');
    console.log('📨 [PreHireChat] Payload completo:', JSON.stringify(payload, null, 2));
    
    // ✅ IMPORTANTE: El backend envía en PascalCase
    const messageDto: Message = {
      id: payload.Id,                    // ✅ PascalCase
      conversationId: payload.ConversationId,  // ✅ PascalCase
      senderId: payload.SenderId,        // ✅ PascalCase
      content: payload.Content,           // ✅ PascalCase
      sentAt: payload.SentAt,            // ✅ PascalCase
      isRead: payload.IsRead,            // ✅ PascalCase
      senderName: payload.SenderName,     // ✅ PascalCase
      locationLatitude: payload.LocationLatitude,  // ✅ PascalCase
      locationLongitude: payload.LocationLongitude, // ✅ PascalCase
      attachmentUrls: payload.AttachmentUrls || []  // ✅ PascalCase
    };
    
    // ✅ Verificar que es para esta conversación
    if (messageDto.conversationId !== conversationId) {
      console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
      return;
    }
    
    // ✅ Agregar mensaje al estado
    setMessages(prev => {
      if (prev.some(m => m.id === messageDto.id)) {
        console.log('⚠️ [PreHireChat] Mensaje duplicado, ignorando');
        return prev;
      }
      
      const updated = [...prev, messageDto].sort((a, b) => 
        new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
      );
      
      console.log('✅ [PreHireChat] Mensaje agregado. Total:', updated.length);
      return updated;
    });
  }
)
```

---

## 🔍 VERIFICACIÓN: ¿Los Broadcasts se Están Enviando?

### **1. Verificar Logs del Backend**

Busca en los logs del backend cuando se envía un mensaje:

```
Message notification sent via Supabase Realtime
Message {Id} notification sent to conversation {ConversationId}
```

O si hay error:

```
Error broadcasting to Supabase Realtime: {StatusCode} - {Error}
Supabase Realtime broadcast warning
```

### **2. Verificar que el Broadcast Llega a Supabase**

El backend hace un POST a:
```
POST https://rveqsehzlvbttlpmsbmi.supabase.co/realtime/v1/api/broadcast
```

**Verifica en los logs del backend:**
- ¿Se está llamando a `NotifyNewMessageAsync`?
- ¿Hay algún error en `BroadcastToChannelAsync`?
- ¿El status code es 200 OK?

### **3. Verificar en el Frontend**

Agrega estos logs en el frontend para verificar que el canal está correcto:

```typescript
useEffect(() => {
  if (!conversationId) return;
  
  const channelName = `conversation:${conversationId}`;
  console.log('🔌 [PreHireChat] Conectando a canal:', channelName);
  
  const channel = supabase
    .channel(channelName)
    
    // ✅ Log cuando se suscribe
    .subscribe((status) => {
      console.log(`📡 [PreHireChat] Estado de suscripción: ${status}`);
      console.log(`📡 [PreHireChat] Canal: ${channelName}`);
      
      if (status === 'SUBSCRIBED') {
        console.log('✅ [PreHireChat] Conectado a Supabase Realtime');
        console.log('✅ [PreHireChat] Escuchando broadcasts en canal:', channelName);
        console.log('✅ [PreHireChat] Esperando evento: new_message');
      }
    });
    
  // ✅ Escuchar TODOS los eventos del canal (para debugging)
  channel.on('broadcast', { event: '*' }, ({ event, payload }) => {
    console.log('📡 [PreHireChat] ===== BROADCAST RECIBIDO =====');
    console.log('📡 [PreHireChat] Evento:', event);
    console.log('📡 [PreHireChat] Payload:', JSON.stringify(payload, null, 2));
  });
  
  // ✅ Escuchar específicamente new_message
  channel.on('broadcast', { event: 'new_message' }, ({ payload }) => {
    console.log('📨 [PreHireChat] ===== EVENTO new_message RECIBIDO =====');
    console.log('📨 [PreHireChat] Payload:', JSON.stringify(payload, null, 2));
    // ... manejar mensaje
  });
}, [conversationId]);
```

---

## 🐛 PROBLEMA COMÚN: El Payload Viene Anidado

Supabase puede enviar el payload de forma anidada. Verifica el formato exacto:

```typescript
.on('broadcast', { event: 'new_message' }, (data) => {
  console.log('📨 [PreHireChat] Data completo:', JSON.stringify(data, null, 2));
  
  // ✅ El payload puede venir directamente o anidado
  const payload = data.payload || data;
  
  console.log('📨 [PreHireChat] Payload extraído:', JSON.stringify(payload, null, 2));
  
  // ✅ Leer propiedades en PascalCase
  const messageDto: Message = {
    id: payload.Id || payload.id,
    conversationId: payload.ConversationId || payload.conversationId,
    senderId: payload.SenderId || payload.senderId,
    content: payload.Content || payload.content || '',
    sentAt: payload.SentAt || payload.sentAt,
    isRead: payload.IsRead || payload.isRead || false,
    senderName: payload.SenderName || payload.senderName || null,
    locationLatitude: payload.LocationLatitude || payload.locationLatitude,
    locationLongitude: payload.LocationLongitude || payload.locationLongitude,
    attachmentUrls: payload.AttachmentUrls || payload.attachmentUrls || []
  };
  
  // ... resto del código
});
```

---

## ✅ SOLUCIÓN COMPLETA RECOMENDADA

### **Frontend - PreHireChat.tsx**

```typescript
useEffect(() => {
  if (!conversationId || !token) return;
  
  const channelName = `conversation:${conversationId}`;
  console.log('🔌 [PreHireChat] Conectando a canal:', channelName);
  
  const channel = supabase
    .channel(channelName)
    
    // ✅ SUSCRIPCIÓN: Escuchar TODOS los broadcasts (para debugging)
    .on('broadcast', { event: '*' }, (data) => {
      console.log('📡 [PreHireChat] ===== BROADCAST RECIBIDO (cualquier evento) =====');
      console.log('📡 [PreHireChat] Evento:', data.event);
      console.log('📡 [PreHireChat] Data completo:', JSON.stringify(data, null, 2));
    })
    
    // ✅ SUSCRIPCIÓN: Escuchar específicamente new_message
    .on('broadcast', { event: 'new_message' }, (data) => {
      console.log('📨 [PreHireChat] ===== EVENTO new_message RECIBIDO =====');
      console.log('📨 [PreHireChat] Data completo:', JSON.stringify(data, null, 2));
      
      // ✅ Extraer payload (puede venir anidado o directo)
      const payload = data.payload || data;
      console.log('📨 [PreHireChat] Payload extraído:', JSON.stringify(payload, null, 2));
      
      // ✅ Leer propiedades en PascalCase (como las envía el backend)
      const messageDto: Message = {
        id: payload.Id || payload.id,
        conversationId: payload.ConversationId || payload.conversationId,
        senderId: payload.SenderId || payload.senderId,
        content: payload.Content || payload.content || '',
        sentAt: payload.SentAt || payload.sentAt,
        isRead: payload.IsRead || payload.isRead || false,
        senderName: payload.SenderName || payload.senderName || null,
        locationLatitude: payload.LocationLatitude || payload.locationLatitude,
        locationLongitude: payload.LocationLongitude || payload.locationLongitude,
        attachmentUrls: payload.AttachmentUrls || payload.attachmentUrls || []
      };
      
      // ✅ Verificar que es para esta conversación
      if (messageDto.conversationId !== conversationId) {
        console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
        return;
      }
      
      // ✅ Agregar mensaje al estado
      setMessages(prev => {
        if (prev.some(m => m.id === messageDto.id)) {
          console.log('⚠️ [PreHireChat] Mensaje duplicado, ignorando');
          return prev;
        }
        
        const updated = [...prev, messageDto].sort((a, b) => 
          new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
        );
        
        console.log('✅ [PreHireChat] Mensaje agregado. Total:', updated.length);
        return updated;
      });
    })
    
    .subscribe((status) => {
      console.log(`📡 [PreHireChat] Estado de suscripción: ${status}`);
      console.log(`📡 [PreHireChat] Canal: ${channelName}`);
      
      if (status === 'SUBSCRIBED') {
        console.log('✅ [PreHireChat] Conectado a Supabase Realtime');
        console.log('✅ [PreHireChat] Escuchando broadcasts en canal:', channelName);
        console.log('✅ [PreHireChat] Esperando evento: new_message');
      } else if (status === 'CHANNEL_ERROR') {
        console.error('❌ [PreHireChat] Error en el canal');
      }
    });
  
  channelRef.current = channel;
  
  return () => {
    console.log('🔌 [PreHireChat] Desconectando de Supabase Realtime');
    if (channelRef.current) {
      supabase.removeChannel(channelRef.current);
    }
  };
}, [conversationId, token, supabase]);
```

---

## 🔍 CHECKLIST DE DEBUGGING

1. **Backend:**
   - [ ] Verificar que `NotifyNewMessageAsync` se llama cuando se envía un mensaje
   - [ ] Verificar que no hay errores en los logs del backend
   - [ ] Verificar que el status code de la petición a Supabase es 200 OK

2. **Frontend:**
   - [ ] Verificar que el canal es `conversation:{conversationId}` (no `messages:conversation:...`)
   - [ ] Verificar que el estado de suscripción es `SUBSCRIBED`
   - [ ] Agregar logs para ver TODOS los broadcasts recibidos (evento `*`)
   - [ ] Verificar que las propiedades se leen en PascalCase
   - [ ] Verificar el formato exacto del payload con logs detallados

3. **Supabase:**
   - [ ] Verificar que la URL de Supabase es correcta
   - [ ] Verificar que la API key es correcta
   - [ ] Verificar que no hay problemas de red/CSP

---

## 🎯 RESUMEN

**El backend está enviando broadcasts correctamente.** El problema puede ser:

1. **Formato del payload:** El frontend debe leer propiedades en **PascalCase** (como las envía el backend)
2. **Formato anidado:** El payload puede venir anidado, verificar con logs
3. **Canal incorrecto:** Asegurarse de que el canal es `conversation:{id}` (no `messages:conversation:{id}`)

**Solución:** Agregar logs detallados para ver exactamente qué formato tiene el payload cuando llega al frontend, y ajustar el código para leer las propiedades correctamente.

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Análisis completo - Listo para debugging
