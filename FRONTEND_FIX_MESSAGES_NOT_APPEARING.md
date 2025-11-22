# 🔧 Solución: Mensajes No Aparecen en el Chat

## 🐛 Problema
Los mensajes nuevos no se ven en el chat hasta que recargas la página.

## ✅ Solución

### 1. Verificar que estás escuchando el evento `ReceiveMessage`

El backend envía los mensajes a través del evento `ReceiveMessage` de SignalR. Asegúrate de que tu código frontend esté escuchando este evento:

```typescript
// ✅ CORRECTO: Escuchar el evento ReceiveMessage
connection.on("ReceiveMessage", (message: MessageDto) => {
  console.log("Nuevo mensaje recibido:", message);
  // Agregar el mensaje a tu lista de mensajes
  setMessages((prev) => [...prev, message]);
  // O si usas un array:
  // messages.push(message);
  // renderMessages();
});
```

### 2. Verificar que estás en el grupo correcto de SignalR

**IMPORTANTE**: Debes llamar a `JoinConversation` **después** de que la conexión esté establecida:

```typescript
connection.start()
  .then(() => {
    console.log("Conectado a SignalR");
    // ✅ IMPORTANTE: Unirse a la conversación DESPUÉS de conectar
    return connection.invoke("JoinConversation", conversationId);
  })
  .then(() => {
    console.log("Unido a la conversación");
  })
  .catch((error) => {
    console.error("Error:", error);
  });
```

### 3. Verificar que el evento se registra ANTES de iniciar la conexión

```typescript
// ✅ CORRECTO: Registrar eventos ANTES de .start()
connection.on("ReceiveMessage", (message) => {
  // Manejar mensaje
});

connection.on("MessageRead", (messageId) => {
  // Manejar mensaje leído
});

// Ahora sí iniciar la conexión
connection.start()
  .then(() => connection.invoke("JoinConversation", conversationId));
```

### 4. Ejemplo Completo Corregido

```typescript
import * as signalR from "@microsoft/signalr";

const setupChatConnection = (conversationId: number) => {
  const token = localStorage.getItem("authToken");
  if (!token) {
    console.error("Token no encontrado");
    return;
  }

  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_URL}/chatHub`, {
      accessTokenFactory: () => token,
      transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling
    })
    .withAutomaticReconnect()
    .build();

  // ✅ PASO 1: Registrar TODOS los eventos ANTES de iniciar
  connection.on("ReceiveMessage", (message: MessageDto) => {
    console.log("📨 Nuevo mensaje recibido:", message);
    // Agregar mensaje a tu estado/lista
    setMessages((prev) => {
      // Evitar duplicados
      if (prev.some(m => m.id === message.id)) {
        return prev;
      }
      return [...prev, message];
    });
  });

  connection.on("MessageRead", (messageId: number) => {
    console.log("✅ Mensaje leído:", messageId);
    setMessages((prev) =>
      prev.map((msg) => (msg.id === messageId ? { ...msg, isRead: true } : msg))
    );
  });

  connection.onreconnected((connectionId) => {
    console.log("🔄 Reconectado, re-uniéndose a la conversación...");
    // ✅ IMPORTANTE: Re-join después de reconectar
    connection.invoke("JoinConversation", conversationId).catch(console.error);
  });

  // ✅ PASO 2: Iniciar conexión
  connection.start()
    .then(() => {
      console.log("✅ Conectado a SignalR");
      // ✅ PASO 3: Unirse a la conversación
      return connection.invoke("JoinConversation", conversationId);
    })
    .then(() => {
      console.log("✅ Unido a la conversación", conversationId);
    })
    .catch((error) => {
      console.error("❌ Error:", error);
    });

  return connection;
};
```

### 5. Debugging: Verificar que recibes mensajes

Agrega logs para verificar que los mensajes llegan:

```typescript
connection.on("ReceiveMessage", (message) => {
  console.log("🔔 EVENTO ReceiveMessage recibido:", message);
  console.log("📋 Datos del mensaje:", {
    id: message.id,
    content: message.content,
    senderId: message.senderId,
    attachmentUrls: message.attachmentUrls
  });
  
  // Tu lógica aquí...
});
```

### 6. Verificar en la Consola del Navegador

Abre las DevTools (F12) y verifica:

1. **Console**: Debe mostrar "📨 Nuevo mensaje recibido" cuando llegue un mensaje
2. **Network**: Verifica que la conexión WebSocket esté activa
3. **No debe haber errores** relacionados con SignalR

### 7. Problemas Comunes

#### ❌ Error: "Cannot send data if the connection is not in the 'Connected' State"
**Solución**: Espera a que la conexión esté conectada antes de enviar mensajes:

```typescript
if (connection.state === signalR.HubConnectionState.Connected) {
  await connection.invoke("JoinConversation", conversationId);
} else {
  connection.start().then(() => {
    connection.invoke("JoinConversation", conversationId);
  });
}
```

#### ❌ Los mensajes aparecen duplicados
**Solución**: Verifica que no estés agregando el mensaje dos veces:

```typescript
connection.on("ReceiveMessage", (message) => {
  setMessages((prev) => {
    // ✅ Evitar duplicados
    if (prev.some(m => m.id === message.id)) {
      return prev;
    }
    return [...prev, message];
  });
});
```

### 8. Checklist de Verificación

- [ ] El evento `ReceiveMessage` está registrado **ANTES** de `connection.start()`
- [ ] Llamas a `JoinConversation(conversationId)` **DESPUÉS** de que la conexión esté conectada
- [ ] Re-join después de reconectar en `onreconnected`
- [ ] No hay errores en la consola del navegador
- [ ] La conexión WebSocket está activa (ver Network tab)
- [ ] El mensaje se agrega correctamente a tu estado/lista

### 9. Si Aún No Funciona

1. **Verifica los logs del servidor**: El backend loguea cuando envía mensajes por SignalR
2. **Verifica que el mensaje se guardó**: Aunque no aparezca en tiempo real, debe estar en la BD
3. **Verifica el grupo de SignalR**: Asegúrate de que estás en el grupo `conversation-{conversationId}`

## 📚 Referencias

- Ver `FRONTEND_SIGNALR_CHAT_GUIDE_2025.md` para la guía completa
- Ver `FRONTEND_UPDATE_JOINCONVERSATION.md` para cambios en la API

