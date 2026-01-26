# 🔍 Análisis: Frontend No Recibe Broadcasts de Supabase

## 📋 Análisis del Código Frontend

### ✅ **Lo que está CORRECTO:**

1. **Canal correcto:** `conversation:${conversation.id}` (línea 315) ✅
2. **Evento correcto:** `new_message` (línea 350) ✅
3. **Lectura de propiedades:** Lee en PascalCase correctamente (líneas 214-225) ✅
4. **Suscripción:** Está suscrito correctamente (línea 435) ✅

### ⚠️ **PROBLEMAS IDENTIFICADOS:**

#### **1. Problema con `handleNewMessage` y `conversation?.id`**

**Línea 150-161:**
```typescript
const handleNewMessage = useRef((messageData: any) => {
  // ...
  const msgConversationId = messageData.ConversationId || messageData.conversationId;
  
  if (msgConversationId !== conversation?.id) {  // ⚠️ PROBLEMA: conversation puede ser null
    console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
    return;
  }
  // ...
}).current;
```

**Problema:** `conversation` puede ser `null` o `undefined` cuando se llama `handleNewMessage`, especialmente si el broadcast llega antes de que la conversación se cargue completamente.

**Solución:** Usar `conversationId` directamente en lugar de `conversation?.id`:

```typescript
const handleNewMessage = useRef((messageData: any) => {
  // ✅ Usar conversationId directamente (viene del useEffect)
  const msgConversationId = messageData.ConversationId || messageData.conversationId;
  
  // ✅ Comparar con conversationId del scope, no conversation?.id
  if (msgConversationId !== conversationId) {  // ✅ CORREGIDO
    console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
    return;
  }
  // ...
}).current;
```

**Pero hay otro problema:** `handleNewMessage` está definido como `useRef` pero se usa dentro del `useEffect` que depende de `conversation?.id`. Esto puede causar que la función capture un valor obsoleto.

#### **2. Problema con la Dependencia del useEffect**

**Línea 478:**
```typescript
}, [conversation?.id, token, supabase, handleNewMessage]);
```

**Problema:** `handleNewMessage` es un `useRef` que no cambia, pero la función interna usa `conversation` que puede cambiar. Esto puede causar que la función capture valores obsoletos.

**Solución:** Mover `handleNewMessage` dentro del `useEffect` o usar `useCallback`:

```typescript
// ✅ MEJOR: Mover handleNewMessage dentro del useEffect
useEffect(() => {
  if (!conversation?.id || !token) return;
  
  // ✅ Definir handleNewMessage dentro del useEffect para capturar conversationId actual
  const handleNewMessage = (messageData: any) => {
    const msgConversationId = messageData.ConversationId || messageData.conversationId;
    
    if (msgConversationId !== conversation.id) {  // ✅ Ahora conversation.id está disponible
      console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
      return;
    }
    
    // ... resto del código
  };
  
  // ... resto del código de suscripción usando handleNewMessage
}, [conversation?.id, token, supabase]);
```

#### **3. Verificar que el Backend Está Enviando Correctamente**

El backend envía el broadcast así:
```csharp
await _realtimeService.NotifyNewMessageAsync(dto.ConversationId, messageDto);
```

Donde `messageDto` tiene propiedades en **PascalCase**:
- `Id`, `ConversationId`, `SenderId`, `Content`, `SentAt`, `IsRead`, `SenderName`, etc.

El frontend lee correctamente en PascalCase, así que eso está bien.

---

## ✅ SOLUCIÓN COMPLETA RECOMENDADA

### **Cambios en PreHireChat.tsx:**

```typescript
// ✅ SUSCRIPCIÓN: Mover handleNewMessage dentro del useEffect
useEffect(() => {
  if (!conversation?.id || !token) {
    console.warn('⚠️ [PreHireChat] No conversationId o token, no se puede conectar');
    return;
  }

  console.log('🔌 [PreHireChat] ===== INICIANDO SUSCRIPCIÓN REALTIME =====');
  console.log('🔌 [PreHireChat] Conectando a Supabase Realtime con autenticación para conversación:', conversation.id);
  
  // ✅ Definir handleNewMessage dentro del useEffect para capturar conversation.id actual
  const handleNewMessage = (messageData: any) => {
    console.log('🔍 [PreHireChat] handleNewMessage llamado con:', messageData);
    
    // ✅ Verificar que es para esta conversación
    const msgConversationId = messageData.ConversationId || messageData.conversationId;
    console.log('🔍 [PreHireChat] ConversationId del mensaje:', msgConversationId);
    console.log('🔍 [PreHireChat] ConversationId esperado:', conversation.id);
    
    if (msgConversationId !== conversation.id) {
      console.warn('⚠️ [PreHireChat] Mensaje de otra conversación, ignorando');
      return;
    }
    
    console.log('✅ [PreHireChat] Mensaje es para esta conversación, procesando...');
    
    // ✅ Crear objeto MessageDto (leer en PascalCase como envía el backend)
    const messageDto: Message = {
      id: messageData.Id || messageData.id,
      conversationId: msgConversationId,
      senderId: messageData.SenderId ?? messageData.senderId ?? null,
      content: messageData.Content || messageData.content || '',
      sentAt: messageData.SentAt || messageData.sentAt,
      isRead: messageData.IsRead ?? messageData.isRead ?? false,
      senderName: messageData.SenderName || messageData.senderName || '[Usuario]',
      locationLatitude: messageData.LocationLatitude ?? messageData.locationLatitude ?? null,
      locationLongitude: messageData.LocationLongitude ?? messageData.locationLongitude ?? null,
      attachmentUrls: messageData.AttachmentUrls || messageData.attachmentUrls || []
    };
    
    // ✅ Agregar mensaje al estado (evitar duplicados)
    setMessages(prev => {
      const existingIndex = prev.findIndex(m => m.id === messageDto.id);
      
      if (existingIndex !== -1) {
        console.log('⚠️ [PreHireChat] Mensaje duplicado detectado:', messageDto.id);
        if (prev[existingIndex].isOptimistic) {
          console.log('🔄 [PreHireChat] Reemplazando mensaje optimístico con mensaje real');
          const updated = [...prev];
          updated[existingIndex] = messageDto;
          return updated.sort((a, b) => 
            new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
          );
        }
        return prev;
      }

      // ✅ Buscar mensaje optimístico por contenido
      const optimisticIndex = prev.findIndex(m => 
        m.isOptimistic && 
        m.content === messageDto.content && 
        m.senderId === messageDto.senderId &&
        Math.abs(new Date(m.sentAt).getTime() - new Date(messageDto.sentAt).getTime()) < 5000
      );

      if (optimisticIndex !== -1) {
        console.log('🔄 [PreHireChat] Reemplazando mensaje optimístico con mensaje real');
        const updated = [...prev];
        updated[optimisticIndex] = messageDto;
        return updated.sort((a, b) => 
          new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
        );
      }

      // ✅ Agregar nuevo mensaje
      const updated = [...prev, messageDto].sort((a, b) => 
        new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
      );

      console.log('✅ [PreHireChat] Mensaje agregado. Total mensajes:', updated.length);
      return updated;
    });
  };

  // ✅ Setup de suscripción (igual que antes)
  const setupSubscription = async () => {
    // ... código de setup igual que antes ...
    
    const channelName = `conversation:${conversation.id}`;
    console.log(`📡 [PreHireChat] Nombre del canal: ${channelName}`);
  
    const channel = supabase
      .channel(channelName)
      
      // ✅ SUSCRIPCIÓN: broadcast (recibe broadcasts del backend)
      .on(
        'broadcast',
        { event: 'new_message' },
        ({ payload }) => {
          console.log('📨 [PreHireChat] ===== EVENTO RECIBIDO (broadcast) =====');
          console.log('📨 [PreHireChat] Payload completo:', JSON.stringify(payload, null, 2));
          console.log('📨 [PreHireChat] SenderId del broadcast:', payload?.SenderId || payload?.senderId);
          console.log('📨 [PreHireChat] Current userId:', userId);
          console.log('📨 [PreHireChat] ¿Es mensaje propio?', (payload?.SenderId || payload?.senderId) === userId);
          
          // ✅ Llamar handleNewMessage con el payload
          handleNewMessage(payload);
        }
      )
      
      // ✅ SUSCRIPCIÓN: Escuchar TODOS los broadcasts para debug
      .on(
        'broadcast',
        { event: '*' },
        ({ event, payload }) => {
          console.log('🔍 [PreHireChat] ===== BROADCAST RECIBIDO (cualquier evento) =====');
          console.log('🔍 [PreHireChat] Evento:', event);
          console.log('🔍 [PreHireChat] Payload:', JSON.stringify(payload, null, 2));
          
          if (event === 'new_message') {
            console.log('🔍 [PreHireChat] Procesando new_message desde listener de *');
            handleNewMessage(payload);
          }
        }
      )
      
      .subscribe((status, err) => {
        // ... código de subscribe igual que antes ...
      });

    channelRef.current = channel;
  };
  
  setupSubscription().catch(err => {
    console.error('❌ [PreHireChat] Error en setupSubscription:', err);
  });

  return () => {
    console.log('🔌 [PreHireChat] Desconectando de Supabase Realtime');
    if (channelRef.current) {
      supabase.removeChannel(channelRef.current);
    }
  };
}, [conversation?.id, token, supabase, userId]); // ✅ Remover handleNewMessage de las dependencias
```

---

## 🔍 VERIFICACIÓN DEL BACKEND

### **Verificar que el Backend Está Enviando:**

1. **Revisar logs del backend** cuando se envía un mensaje:
   - Buscar: `"Message notification sent via Supabase Realtime"`
   - O: `"Error broadcasting to Supabase Realtime"`

2. **Verificar el formato del payload:**
   - El backend envía `MessageDto` con propiedades en PascalCase
   - El formato JSON debe ser: `{ "Id": 123, "ConversationId": 61, "SenderId": 13, ... }`

3. **Verificar que el canal es correcto:**
   - Backend envía a: `conversation:{conversationId}`
   - Frontend escucha en: `conversation:{conversationId}` ✅

---

## 🐛 DEBUGGING ADICIONAL

### **Agregar Logs en el Backend:**

Modificar `Services/SupabaseRealtimeService.cs` para agregar más logs:

```csharp
public async Task NotifyNewMessageAsync(int conversationId, object messageData)
{
    var channel = $"conversation:{conversationId}";
    
    // ✅ AGREGAR LOGS
    _logger.LogInformation("🔔 [SupabaseRealtime] Enviando broadcast a canal: {Channel}, evento: new_message", channel);
    _logger.LogInformation("🔔 [SupabaseRealtime] Payload: {Payload}", JsonSerializer.Serialize(messageData));
    
    await BroadcastToChannelAsync(channel, "new_message", messageData);
    
    _logger.LogInformation("✅ [SupabaseRealtime] Broadcast enviado exitosamente");
}
```

### **Verificar en el Frontend:**

Agrega estos logs para verificar que los broadcasts llegan:

```typescript
.on(
  'broadcast',
  { event: '*' },
  ({ event, payload }) => {
    console.log('🔍 [PreHireChat] ===== BROADCAST RECIBIDO (cualquier evento) =====');
    console.log('🔍 [PreHireChat] Evento:', event);
    console.log('🔍 [PreHireChat] Payload completo:', JSON.stringify(payload, null, 2));
    console.log('🔍 [PreHireChat] Tipo de payload:', typeof payload);
    console.log('🔍 [PreHireChat] ¿Tiene Id?', 'Id' in payload || 'id' in payload);
    console.log('🔍 [PreHireChat] ¿Tiene ConversationId?', 'ConversationId' in payload || 'conversationId' in payload);
  }
)
```

---

## ✅ CHECKLIST DE CORRECCIÓN

- [ ] Mover `handleNewMessage` dentro del `useEffect` para capturar `conversation.id` actual
- [ ] Cambiar `conversation?.id` por `conversation.id` en la comparación (ya está dentro del useEffect)
- [ ] Remover `handleNewMessage` de las dependencias del `useEffect`
- [ ] Agregar logs detallados en el backend para verificar que se envían broadcasts
- [ ] Agregar logs detallados en el frontend para verificar que se reciben broadcasts
- [ ] Verificar que el formato del payload coincide entre backend y frontend
- [ ] Probar enviando mensajes entre dos usuarios diferentes

---

## 🎯 RESUMEN

**Problema principal:** `handleNewMessage` está definido como `useRef` fuera del `useEffect`, lo que puede causar que capture valores obsoletos de `conversation?.id`.

**Solución:** Mover `handleNewMessage` dentro del `useEffect` para que capture el valor actual de `conversation.id`.

**Verificación adicional:** Agregar logs en backend y frontend para confirmar que los broadcasts se envían y reciben correctamente.

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Análisis completo - Listo para implementar correcciones
