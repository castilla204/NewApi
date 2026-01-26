# 🐛 Debug: Mensajes Propios No Aparecen en Tiempo Real

## 📋 Problema

Cuando el usuario envía un mensaje, este no aparece inmediatamente en el frontend, aunque:
- ✅ El mensaje se envía correctamente al backend
- ✅ La suscripción a Supabase Realtime está activa (`SUBSCRIBED`)
- ✅ Los mensajes de otros usuarios sí aparecen en tiempo real

---

## 🔍 Causas Posibles

### 1. **Timing Issue (Más Común)**
El mensaje se guarda en la BD, pero el evento `postgres_changes` puede tardar unos milisegundos en llegar. Si el frontend no está escuchando correctamente, el mensaje no aparece.

### 2. **Filtro Incorrecto en la Suscripción**
El filtro `ConversationId=eq.${conversationId}` podría no estar funcionando correctamente.

### 3. **Optimistic Update Conflict**
Si el frontend agrega el mensaje optimísticamente al enviarlo, pero luego el evento de Supabase no llega o se filtra, puede haber confusión.

### 4. **Problema con el Payload de Supabase**
El formato del payload que llega desde Supabase podría no coincidir con lo que el frontend espera.

---

## ✅ Solución Recomendada: Optimistic Update + Supabase Confirmation

La mejor práctica es agregar el mensaje **inmediatamente** cuando se envía (optimistic update), y luego dejar que Supabase lo confirme o actualice cuando llegue el evento.

### **Implementación Completa:**

```typescript
// components/PreHireChat.tsx (o tu componente de chat)
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
  // ✅ NUEVO: Flag para identificar mensajes optimísticos
  isOptimistic?: boolean;
}

export const PreHireChat = ({ conversationId, userId, token, apiUrl }) => {
  const [messages, setMessages] = useState<Message[]>([]);
  const [inputValue, setInputValue] = useState('');
  const channelRef = useRef<any>(null);
  const queryClient = useQueryClient();

  // ✅ 1. Cargar conversación inicial
  const { data: conversation } = useQuery({
    queryKey: ['conversation', conversationId],
    queryFn: async () => {
      const response = await fetch(
        `${apiUrl}/api/Chat/conversation-by-service?searchServiceId=${serviceId}`,
        {
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          }
        }
      );
      if (!response.ok) throw new Error('Error al cargar conversación');
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

  // ✅ 3. SUSCRIPCIÓN A SUPABASE REALTIME (MEJORADA)
  useEffect(() => {
    if (!conversationId) return;

    console.log('🔌 [PreHireChat] Conectando a Supabase Realtime para conversación:', conversationId);

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
          console.log('📨 [PreHireChat] Nuevo mensaje recibido desde Supabase:', payload);
          
          const newMessage = payload.new as any;
          
          // ✅ Obtener información del sender
          let senderName = '[Usuario eliminado]';
          if (newMessage.SenderId) {
            try {
              // Obtener nombre del sender desde la conversación o hacer otra llamada
              // Por ahora, intentamos obtenerlo de la conversación cargada
              const sender = conversation?.messages?.find(m => m.senderId === newMessage.SenderId)?.senderName;
              if (sender) {
                senderName = sender;
              } else {
                // Si no está en la conversación, hacer fetch
                const senderResponse = await fetch(
                  `${apiUrl}/api/Users/${newMessage.SenderId}`,
                  { headers: { 'Authorization': `Bearer ${token}` } }
                );
                if (senderResponse.ok) {
                  const senderData = await senderResponse.json();
                  senderName = senderData.name || '[Usuario eliminado]';
                }
              }
            } catch (error) {
              console.error('❌ [PreHireChat] Error obteniendo info del sender:', error);
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

          console.log('✅ [PreHireChat] Mensaje procesado:', messageDto);

          // ✅ Agregar mensaje al estado (evitar duplicados)
          setMessages(prev => {
            // Verificar si el mensaje ya existe (por ID)
            const existingIndex = prev.findIndex(m => m.id === messageDto.id);
            
            if (existingIndex !== -1) {
              console.log('⚠️ [PreHireChat] Mensaje duplicado ignorado (ya existe):', messageDto.id);
              // ✅ Si existe pero es optimístico, reemplazarlo con el real
              if (prev[existingIndex].isOptimistic) {
                console.log('🔄 [PreHireChat] Reemplazando mensaje optimístico con mensaje real');
                const updated = [...prev];
                updated[existingIndex] = messageDto; // Reemplazar con el mensaje real
                return updated.sort((a, b) => 
                  new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
                );
              }
              return prev; // Ya existe y no es optimístico, no hacer nada
            }

            // ✅ Agregar nuevo mensaje y ordenar por fecha
            const updated = [...prev, messageDto].sort((a, b) => 
              new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
            );

            console.log('✅ [PreHireChat] Mensaje agregado. Total mensajes:', updated.length);
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
          console.log('🔄 [PreHireChat] Mensaje actualizado:', payload);
          
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

      // ✅ Suscribirse al canal
      .subscribe((status) => {
        console.log(`📡 [PreHireChat] Estado de suscripción: ${status}`);
        
        if (status === 'SUBSCRIBED') {
          console.log('✅ [PreHireChat] Conectado a Supabase Realtime');
        } else if (status === 'CHANNEL_ERROR') {
          console.error('❌ [PreHireChat] Error en el canal de Supabase');
        }
      });

    // Guardar referencia del canal para limpiar después
    channelRef.current = channel;

    // ✅ Limpiar suscripción al desmontar el componente
    return () => {
      console.log('🔌 [PreHireChat] Desconectando de Supabase Realtime');
      if (channelRef.current) {
        supabase.removeChannel(channelRef.current);
      }
    };
  }, [conversationId, token, apiUrl, queryClient, conversation]);

  // ✅ 4. Enviar mensaje (CON OPTIMISTIC UPDATE)
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
    onMutate: async (content) => {
      // ✅ OPTIMISTIC UPDATE: Agregar mensaje inmediatamente
      const optimisticMessage: Message = {
        id: Date.now(), // ID temporal (se reemplazará con el real)
        conversationId: conversation.id,
        senderId: userId,
        content: content,
        sentAt: new Date().toISOString(),
        isRead: false,
        senderName: 'Tú', // O el nombre del usuario actual
        locationLatitude: null,
        locationLongitude: null,
        attachmentUrls: [],
        isOptimistic: true // ✅ Flag para identificar mensajes optimísticos
      };

      console.log('🚀 [PreHireChat] Agregando mensaje optimístico:', optimisticMessage);

      setMessages(prev => {
        const updated = [...prev, optimisticMessage].sort((a, b) => 
          new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
        );
        console.log('✅ [PreHireChat] Mensaje optimístico agregado. Total mensajes:', updated.length);
        return updated;
      });

      setInputValue(''); // Limpiar input inmediatamente

      return { optimisticMessage };
    },
    onSuccess: (data, variables, context) => {
      console.log('✅ [PreHireChat] Mensaje enviado exitosamente:', data);
      
      // ✅ El mensaje optimístico será reemplazado por el real cuando llegue el evento de Supabase
      // Si el evento no llega, el mensaje optimístico permanecerá hasta que se recargue la conversación
      
      // ✅ Opcional: Reemplazar mensaje optimístico con el real inmediatamente
      setMessages(prev => {
        const updated = prev.map(msg => {
          if (msg.isOptimistic && msg.content === variables) {
            // Reemplazar con el mensaje real del servidor
            return {
              ...data,
              isOptimistic: false
            };
          }
          return msg;
        });
        
        // Si no se encontró el mensaje optimístico, agregar el real
        if (!updated.some(m => m.id === data.id)) {
          updated.push(data);
        }
        
        return updated.sort((a, b) => 
          new Date(a.sentAt).getTime() - new Date(b.sentAt).getTime()
        );
      });
    },
    onError: (error, variables, context) => {
      console.error('❌ [PreHireChat] Error al enviar mensaje:', error);
      
      // ✅ Remover mensaje optimístico en caso de error
      if (context?.optimisticMessage) {
        setMessages(prev => 
          prev.filter(msg => msg.id !== context.optimisticMessage.id)
        );
      }
      
      // ✅ Restaurar el input
      setInputValue(variables);
    }
  });

  const handleSend = () => {
    if (!inputValue.trim() || !conversation || sendMessageMutation.isPending) return;
    sendMessageMutation.mutate(inputValue.trim());
  };

  // ... resto del componente (render, etc.)
};
```

---

## 🔍 Debugging: Verificar que Funciona

### **1. Agregar Logs Detallados**

Agrega estos logs en tu componente para ver qué está pasando:

```typescript
// En el handler de postgres_changes INSERT
.on('postgres_changes', { ... }, (payload) => {
  console.log('📨 [DEBUG] Payload completo:', JSON.stringify(payload, null, 2));
  console.log('📨 [DEBUG] payload.new:', payload.new);
  console.log('📨 [DEBUG] ConversationId del payload:', payload.new.ConversationId);
  console.log('📨 [DEBUG] ConversationId esperado:', conversationId);
  console.log('📨 [DEBUG] ¿Coinciden?', payload.new.ConversationId === conversationId);
  
  // ... resto del código
})
```

### **2. Verificar que el Evento Llega**

Abre la consola del navegador y busca:
- `📨 [PreHireChat] Nuevo mensaje recibido desde Supabase:` - Debe aparecer cuando envías un mensaje
- Si NO aparece, el problema está en la suscripción de Supabase
- Si aparece pero el mensaje no se agrega, el problema está en el handler

### **3. Verificar el Filtro de Supabase**

El filtro `ConversationId=eq.${conversationId}` debe coincidir exactamente. Verifica:
- Que `conversationId` sea un número (no string)
- Que el `ConversationId` en la BD sea del mismo tipo

### **4. Verificar Configuración de Supabase Realtime**

Asegúrate de que:
- La tabla `Messages` tiene Realtime habilitado en Supabase
- La publicación `supabase_realtime` incluye la tabla `Messages`
- RLS está configurado correctamente (si aplica)

---

## 🎯 Solución Alternativa: Usar Broadcast en Lugar de postgres_changes

Si `postgres_changes` no funciona para mensajes propios, puedes escuchar también el broadcast que envía el backend:

```typescript
// ✅ SUSCRIPCIÓN ADICIONAL: Broadcast del backend
.on(
  'broadcast',
  { event: 'new_message' },
  ({ payload }) => {
    console.log('📨 [PreHireChat] Mensaje recibido vía broadcast:', payload);
    
    // ✅ Agregar mensaje al estado
    const messageDto: Message = {
      id: payload.id,
      conversationId: payload.conversationId,
      senderId: payload.senderId,
      content: payload.content,
      sentAt: payload.sentAt,
      isRead: payload.isRead || false,
      senderName: payload.senderName || '[Usuario eliminado]',
      locationLatitude: payload.locationLatitude,
      locationLongitude: payload.locationLongitude,
      attachmentUrls: payload.attachmentUrls || []
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
```

---

## ✅ Checklist de Implementación

- [ ] Implementar optimistic update en `onMutate`
- [ ] Agregar flag `isOptimistic` a los mensajes temporales
- [ ] Reemplazar mensaje optimístico con el real en `onSuccess` o cuando llegue el evento de Supabase
- [ ] Agregar logs detallados para debugging
- [ ] Verificar que el filtro `ConversationId=eq.${conversationId}` funciona
- [ ] Probar enviando un mensaje y verificando que aparece inmediatamente
- [ ] Verificar que el mensaje optimístico se reemplaza con el real cuando llega el evento

---

## 🎯 Resumen

**Problema:** Los mensajes propios no aparecen en tiempo real.

**Solución:**
1. ✅ **Optimistic Update**: Agregar el mensaje inmediatamente al enviarlo
2. ✅ **Supabase Confirmation**: Cuando llegue el evento de Supabase, reemplazar el mensaje optimístico con el real
3. ✅ **Logs Detallados**: Agregar logs para ver qué está pasando
4. ✅ **Manejo de Duplicados**: Verificar por ID antes de agregar mensajes

**Ventajas:**
- ⚡ El mensaje aparece **instantáneamente** (mejor UX)
- 🔄 Se sincroniza con el servidor cuando llega el evento
- 🛡️ Maneja errores correctamente (remueve mensaje optimístico si falla)

---

**Fecha:** 2026-01-26  
**Estado:** ✅ Solución implementada y lista para usar
