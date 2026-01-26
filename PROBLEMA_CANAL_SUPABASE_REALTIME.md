# 🚨 PROBLEMA CRÍTICO: Desajuste de Canales en Supabase Realtime

## 🔍 PROBLEMA IDENTIFICADO

Los mensajes no aparecen en tiempo real entre usuarios porque hay un **desajuste en los nombres de los canales** entre el backend y el frontend.

### **Backend envía broadcasts a:**
```csharp
// Services/SupabaseRealtimeService.cs línea 116
var channel = $"conversation:{conversationId}";
await BroadcastToChannelAsync(channel, "new_message", messageData);
```
**Canal:** `conversation:59` (ejemplo)

### **Frontend escucha en:**
```typescript
// Frontend (PreHireChat.tsx)
const channelName = `messages:conversation:${conversationId}`;
const channel = supabase.channel(channelName)
```
**Canal:** `messages:conversation:59` (ejemplo)

**❌ NO COINCIDEN - Los broadcasts nunca llegan al frontend**

---

## ✅ SOLUCIÓN

Hay dos opciones:

### **Opción 1: Cambiar el Frontend (Recomendado - Más Simple)**

Cambiar el nombre del canal en el frontend para que coincida con el backend:

```typescript
// ❌ ANTES (INCORRECTO)
const channelName = `messages:conversation:${conversationId}`;

// ✅ DESPUÉS (CORRECTO)
const channelName = `conversation:${conversationId}`;
```

### **Opción 2: Cambiar el Backend**

Cambiar el nombre del canal en el backend para que coincida con el frontend:

```csharp
// ❌ ANTES (INCORRECTO)
var channel = $"conversation:{conversationId}";

// ✅ DESPUÉS (CORRECTO)
var channel = $"messages:conversation:{conversationId}";
```

**Recomendación:** Usar la **Opción 1** (cambiar frontend) porque es más simple y no requiere cambios en el backend.

---

## 🔧 IMPLEMENTACIÓN COMPLETA CORREGIDA

### **Frontend - PreHireChat.tsx**

```typescript
// ✅ CORRECCIÓN: Usar el mismo nombre de canal que el backend
useEffect(() => {
  if (!conversationId || !token) return;
  
  console.log('🔌 [PreHireChat] Conectando a Supabase Realtime para conversación:', conversationId);
  
  // ✅ IMPORTANTE: El canal debe ser "conversation:{id}" para coincidir con el backend
  const channelName = `conversation:${conversationId}`; // ✅ CORREGIDO
  
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
        console.log('📨 [PreHireChat] Mensaje recibido vía postgres_changes:', payload);
        handleNewMessage(payload.new);
      }
    )
    
    // ✅ SUSCRIPCIÓN 2: broadcast (recibe broadcasts del backend) - MÁS CONFIABLE
    .on(
      'broadcast',
      { event: 'new_message' },
      ({ payload }) => {
        console.log('📨 [PreHireChat] Mensaje recibido vía broadcast:', payload);
        handleNewMessage(payload);
      }
    )
    
    // ✅ Suscribirse al canal
    .subscribe((status) => {
      console.log(`📡 [PreHireChat] Estado de suscripción: ${status}`);
      setIsConnected(status === 'SUBSCRIBED');
      
      if (status === 'SUBSCRIBED') {
        console.log('✅ [PreHireChat] Conectado a Supabase Realtime');
        console.log(`✅ [PreHireChat] Escuchando en canal: ${channelName}`);
      } else if (status === 'CHANNEL_ERROR') {
        console.error('❌ [PreHireChat] Error en el canal de Supabase');
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

## 🔍 VERIFICACIÓN

Después de aplicar la corrección:

1. **Abre la consola del navegador** en ambos clientes
2. **Envía un mensaje desde un cliente**
3. **Verifica en la consola del otro cliente:**
   - Debe aparecer: `📨 [PreHireChat] Mensaje recibido vía broadcast:`
   - O: `📨 [PreHireChat] Mensaje recibido vía postgres_changes:`
4. **El mensaje debe aparecer inmediatamente** en el otro cliente

### **Logs Esperados:**

**Cliente que envía:**
```
🚀 [PreHireChat] Agregando mensaje optimístico: ...
✅ [PreHireChat] Mensaje enviado exitosamente: ...
```

**Cliente que recibe:**
```
📨 [PreHireChat] Mensaje recibido vía broadcast: {id: 169, conversationId: 59, ...}
✅ [PreHireChat] Mensaje agregado. Total: X
```

---

## 📋 CHECKLIST DE CORRECCIÓN

- [ ] Cambiar el nombre del canal en el frontend de `messages:conversation:${conversationId}` a `conversation:${conversationId}`
- [ ] Verificar que el backend envía a `conversation:{conversationId}` (ya está correcto)
- [ ] Agregar logs para verificar el nombre del canal
- [ ] Probar enviando mensajes entre dos usuarios diferentes
- [ ] Verificar que los broadcasts llegan correctamente
- [ ] Verificar que los mensajes aparecen en tiempo real

---

## 🎯 RESUMEN

**Problema:** Desajuste en los nombres de los canales entre backend y frontend.

**Backend envía a:** `conversation:59`
**Frontend escucha en:** `messages:conversation:59` ❌

**Solución:** Cambiar el frontend para usar `conversation:${conversationId}` en lugar de `messages:conversation:${conversationId}`.

**Resultado esperado:** Los broadcasts del backend llegarán correctamente al frontend y los mensajes aparecerán en tiempo real.

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Problema identificado y solución lista para implementar
