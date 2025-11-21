# 🚀 Guía Completa: Chat en Vivo con SignalR 2025

## 📋 Tabla de Contenidos
1. [Introducción](#introducción)
2. [Configuración Inicial](#configuración-inicial)
3. [Implementación del Cliente SignalR](#implementación-del-cliente-signalr)
4. [Métodos del Hub Disponibles](#métodos-del-hub-disponibles)
5. [Eventos del Hub](#eventos-del-hub)
6. [Reconexión Automática](#reconexión-automática)
7. [Manejo de Errores](#manejo-de-errores)
8. [Ejemplo Completo React/TypeScript](#ejemplo-completo-reacttypescript)
9. [Ejemplo Completo JavaScript Vanilla](#ejemplo-completo-javascript-vanilla)
10. [Mejores Prácticas 2025](#mejores-prácticas-2025)
11. [Troubleshooting](#troubleshooting)

---

## Introducción

Esta guía te ayudará a implementar el chat en vivo usando SignalR con las mejores prácticas de 2025. El backend ya está optimizado con:

- ✅ Reconexión automática mejorada
- ✅ Manejo robusto de errores
- ✅ Estado de usuario (online/offline, typing)
- ✅ Gestión eficiente de grupos y conexiones
- ✅ Logging detallado para debugging

**Endpoint del Hub:** `/chatHub`

**Autenticación:** Requiere JWT token en el query string o header Authorization

---

## Configuración Inicial

### Instalación de Dependencias

#### React/TypeScript
```bash
npm install @microsoft/signalr
# o
yarn add @microsoft/signalr
```

#### JavaScript Vanilla
```html
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>
```

---

## Implementación del Cliente SignalR

### Configuración Base

#### React/TypeScript
```typescript
import * as signalR from "@microsoft/signalr";

const API_BASE_URL = process.env.REACT_APP_API_URL || "http://localhost:7124";
const HUB_URL = `${API_BASE_URL}/chatHub`;

// Función para obtener el token JWT
const getAuthToken = (): string | null => {
  return localStorage.getItem("authToken"); // o donde guardes tu token
};

// Crear conexión SignalR
const createConnection = (): signalR.HubConnection => {
  const token = getAuthToken();
  
  if (!token) {
    throw new Error("Authentication token is required");
  }

  return new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      accessTokenFactory: () => token,
      // ✅ MEJORA 2025: Configurar transporte con fallback
      transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
      // ✅ MEJORA 2025: Timeout de conexión aumentado
      timeout: 30000, // 30 segundos
      // ✅ MEJORA 2025: Configurar headers adicionales si es necesario
      headers: {
        "X-Requested-With": "XMLHttpRequest"
      }
    })
    .withAutomaticReconnect({
      // ✅ MEJORA 2025: Estrategia de reconexión exponencial mejorada
      nextRetryDelayInMilliseconds: (retryContext) => {
        // Primer intento: 0ms (inmediato)
        if (retryContext.previousRetryCount === 0) return 0;
        // Segundo intento: 2 segundos
        if (retryContext.previousRetryCount === 1) return 2000;
        // Tercer intento: 5 segundos
        if (retryContext.previousRetryCount === 2) return 5000;
        // Intentos siguientes: 10 segundos (máximo)
        return Math.min(10000, retryContext.elapsedMilliseconds);
      }
    })
    .configureLogging(signalR.LogLevel.Information) // En desarrollo: Information, en producción: Warning
    .build();
};
```

#### JavaScript Vanilla
```javascript
const API_BASE_URL = "http://localhost:7124"; // o tu URL de producción
const HUB_URL = `${API_BASE_URL}/chatHub`;

// Función para obtener el token JWT
function getAuthToken() {
  return localStorage.getItem("authToken"); // o donde guardes tu token
}

// Crear conexión SignalR
function createConnection() {
  const token = getAuthToken();
  
  if (!token) {
    throw new Error("Authentication token is required");
  }

  return new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      accessTokenFactory: () => token,
      transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
      timeout: 30000
    })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (retryContext) => {
        if (retryContext.previousRetryCount === 0) return 0;
        if (retryContext.previousRetryCount === 1) return 2000;
        if (retryContext.previousRetryCount === 2) return 5000;
        return Math.min(10000, retryContext.elapsedMilliseconds);
      }
    })
    .configureLogging(signalR.LogLevel.Information)
    .build();
}
```

---

## Métodos del Hub Disponibles

### 1. `JoinConversation(conversationId: number)`
Únete a una conversación para recibir mensajes en tiempo real.

```typescript
await connection.invoke("JoinConversation", conversationId);
```

**Parámetros:**
- `conversationId` (number): ID de la conversación

**Errores posibles:**
- `HubException: "Conversation not found"` - La conversación no existe
- `HubException: "You are not authorized to join this conversation"` - No tienes permisos

### 2. `LeaveConversation(conversationId: number)`
Abandona una conversación.

```typescript
await connection.invoke("LeaveConversation", conversationId);
```

### 3. `UserTyping(conversationId: number, isTyping: boolean)`
Indica que estás escribiendo o dejaste de escribir.

```typescript
// Usuario empezó a escribir
await connection.invoke("UserTyping", conversationId, true);

// Usuario dejó de escribir
await connection.invoke("UserTyping", conversationId, false);
```

### 4. `GetOnlineUsers(conversationId: number)`
Obtiene la lista de usuarios online en una conversación.

```typescript
await connection.invoke("GetOnlineUsers", conversationId);
// La respuesta llegará a través del evento "OnlineUsers"
```

---

## Eventos del Hub

### 1. `ReceiveMessage`
Se dispara cuando se recibe un nuevo mensaje en la conversación.

```typescript
connection.on("ReceiveMessage", (message: MessageDto) => {
  console.log("Nuevo mensaje recibido:", message);
  // message: {
  //   id: number;
  //   conversationId: number;
  //   senderId: number | null;
  //   content: string | null;
  //   sentAt: string; // ISO date string
  //   isRead: boolean;
  //   senderName: string | null;
  //   locationLatitude: string | null;
  //   locationLongitude: string | null;
  //   attachmentUrls: string[];
  // }
});
```

### 2. `MessageRead`
Se dispara cuando un mensaje es marcado como leído.

```typescript
connection.on("MessageRead", (messageId: number) => {
  console.log("Mensaje marcado como leído:", messageId);
});
```

### 3. `UserTyping`
Se dispara cuando otro usuario está escribiendo o dejó de escribir.

```typescript
connection.on("UserTyping", (data: { userId: number; conversationId: number; isTyping: boolean }) => {
  if (data.isTyping) {
    console.log(`Usuario ${data.userId} está escribiendo...`);
  } else {
    console.log(`Usuario ${data.userId} dejó de escribir`);
  }
});
```

### 4. `UserJoinedConversation`
Se dispara cuando otro usuario se une a la conversación.

```typescript
connection.on("UserJoinedConversation", (data: { userId: number; conversationId: number }) => {
  console.log(`Usuario ${data.userId} se unió a la conversación ${data.conversationId}`);
});
```

### 5. `UserLeftConversation`
Se dispara cuando otro usuario abandona la conversación.

```typescript
connection.on("UserLeftConversation", (data: { userId: number; conversationId: number }) => {
  console.log(`Usuario ${data.userId} abandonó la conversación ${data.conversationId}`);
});
```

### 6. `OnlineUsers`
Respuesta del método `GetOnlineUsers`.

```typescript
connection.on("OnlineUsers", (data: { conversationId: number; userIds: number[] }) => {
  console.log("Usuarios online:", data.userIds);
});
```

### 7. `ReceiveDeliverable`
Se dispara cuando se sube un entregable (deliverable) a la conversación.

```typescript
connection.on("ReceiveDeliverable", (data: DeliverableResponseDto) => {
  // data: {
  //   searchHireId: number;
  //   deliverableUrls: string[];
  //   createdAt: string; // ISO date string
  // }
});
```

---

## Reconexión Automática

SignalR tiene reconexión automática habilitada por defecto. Puedes escuchar los eventos de estado de conexión:

```typescript
// Estado de la conexión cambió
connection.onreconnecting((error) => {
  console.log("Reconectando...", error);
  // Mostrar indicador de "Reconectando..." en la UI
});

connection.onreconnected((connectionId) => {
  console.log("Reconectado exitosamente. Nueva connectionId:", connectionId);
  // Ocultar indicador de "Reconectando..."
  // Re-join a las conversaciones activas
  // Ejemplo: await connection.invoke("JoinConversation", currentConversationId);
});

connection.onclose((error) => {
  console.log("Conexión cerrada", error);
  // Mostrar indicador de "Desconectado"
  // Intentar reconectar manualmente si es necesario
});
```

---

## Manejo de Errores

### Errores de Conexión

```typescript
connection.start()
  .then(() => {
    console.log("Conectado a SignalR");
  })
  .catch((error) => {
    console.error("Error al conectar:", error);
    
    if (error.message.includes("401") || error.message.includes("Unauthorized")) {
      // Token inválido o expirado - redirigir a login
      console.error("Token inválido. Redirigiendo a login...");
      // window.location.href = "/login";
    } else if (error.message.includes("404")) {
      // Hub no encontrado - verificar URL
      console.error("Hub no encontrado. Verificar URL:", HUB_URL);
    } else {
      // Otro error - intentar reconectar después de un delay
      setTimeout(() => {
        connection.start().catch(console.error);
      }, 5000);
    }
  });
```

### Errores en Invocaciones

```typescript
try {
  await connection.invoke("JoinConversation", conversationId);
} catch (error) {
  if (error instanceof Error) {
    if (error.message.includes("not found")) {
      console.error("Conversación no encontrada");
    } else if (error.message.includes("not authorized")) {
      console.error("No autorizado para unirte a esta conversación");
    } else {
      console.error("Error al unirse a la conversación:", error.message);
    }
  }
}
```

---

## Ejemplo Completo React/TypeScript

```typescript
import React, { useEffect, useState, useRef, useCallback } from "react";
import * as signalR from "@microsoft/signalr";

interface MessageDto {
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

interface ChatProps {
  conversationId: number;
  currentUserId: number;
}

const ChatComponent: React.FC<ChatProps> = ({ conversationId, currentUserId }) => {
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [messages, setMessages] = useState<MessageDto[]>([]);
  const [isConnected, setIsConnected] = useState(false);
  const [isTyping, setIsTyping] = useState(false);
  const [typingUsers, setTypingUsers] = useState<Set<number>>(new Set());
  const [onlineUsers, setOnlineUsers] = useState<number[]>([]);
  const typingTimeoutRef = useRef<NodeJS.Timeout | null>(null);

  // Crear conexión
  useEffect(() => {
    const token = localStorage.getItem("authToken");
    if (!token) {
      console.error("Token no encontrado");
      return;
    }

    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl(`${process.env.REACT_APP_API_URL || "http://localhost:7124"}/chatHub`, {
        accessTokenFactory: () => token,
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
        timeout: 30000
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount === 0) return 0;
          if (retryContext.previousRetryCount === 1) return 2000;
          if (retryContext.previousRetryCount === 2) return 5000;
          return Math.min(10000, retryContext.elapsedMilliseconds);
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    // Eventos de conexión
    newConnection.onreconnecting(() => {
      console.log("Reconectando...");
      setIsConnected(false);
    });

    newConnection.onreconnected((connectionId) => {
      console.log("Reconectado:", connectionId);
      setIsConnected(true);
      // Re-join a la conversación después de reconectar
      newConnection.invoke("JoinConversation", conversationId).catch(console.error);
    });

    newConnection.onclose((error) => {
      console.log("Conexión cerrada", error);
      setIsConnected(false);
    });

    // Eventos del chat
    newConnection.on("ReceiveMessage", (message: MessageDto) => {
      setMessages((prev) => [...prev, message]);
    });

    newConnection.on("MessageRead", (messageId: number) => {
      setMessages((prev) =>
        prev.map((msg) => (msg.id === messageId ? { ...msg, isRead: true } : msg))
      );
    });

    newConnection.on("UserTyping", (data: { userId: number; conversationId: number; isTyping: boolean }) => {
      if (data.conversationId !== conversationId) return;
      
      setTypingUsers((prev) => {
        const newSet = new Set(prev);
        if (data.isTyping) {
          newSet.add(data.userId);
        } else {
          newSet.delete(data.userId);
        }
        return newSet;
      });
    });

    newConnection.on("UserJoinedConversation", (data: { userId: number; conversationId: number }) => {
      if (data.conversationId === conversationId) {
        setOnlineUsers((prev) => {
          if (!prev.includes(data.userId)) {
            return [...prev, data.userId];
          }
          return prev;
        });
      }
    });

    newConnection.on("UserLeftConversation", (data: { userId: number; conversationId: number }) => {
      if (data.conversationId === conversationId) {
        setOnlineUsers((prev) => prev.filter((id) => id !== data.userId));
        setTypingUsers((prev) => {
          const newSet = new Set(prev);
          newSet.delete(data.userId);
          return newSet;
        });
      }
    });

    newConnection.on("OnlineUsers", (data: { conversationId: number; userIds: number[] }) => {
      if (data.conversationId === conversationId) {
        setOnlineUsers(data.userIds);
      }
    });

    // Iniciar conexión
    newConnection
      .start()
      .then(() => {
        console.log("Conectado a SignalR");
        setIsConnected(true);
        // Unirse a la conversación
        return newConnection.invoke("JoinConversation", conversationId);
      })
      .then(() => {
        // Obtener usuarios online
        return newConnection.invoke("GetOnlineUsers", conversationId);
      })
      .catch((error) => {
        console.error("Error al conectar:", error);
      });

    setConnection(newConnection);

    // Cleanup
    return () => {
      if (newConnection.state === signalR.HubConnectionState.Connected) {
        newConnection.invoke("LeaveConversation", conversationId).catch(console.error);
        newConnection.stop().catch(console.error);
      }
    };
  }, [conversationId]);

  // Manejar typing
  const handleTyping = useCallback(() => {
    if (!connection || !isConnected) return;

    // Enviar señal de typing
    if (!isTyping) {
      setIsTyping(true);
      connection.invoke("UserTyping", conversationId, true).catch(console.error);
    }

    // Limpiar timeout anterior
    if (typingTimeoutRef.current) {
      clearTimeout(typingTimeoutRef.current);
    }

    // Después de 3 segundos sin escribir, enviar señal de "dejó de escribir"
    typingTimeoutRef.current = setTimeout(() => {
      if (connection && isConnected) {
        setIsTyping(false);
        connection.invoke("UserTyping", conversationId, false).catch(console.error);
      }
    }, 3000);
  }, [connection, isConnected, conversationId, isTyping]);

  return (
    <div className="chat-container">
      {/* Indicador de conexión */}
      <div className={`connection-status ${isConnected ? "connected" : "disconnected"}`}>
        {isConnected ? "🟢 Conectado" : "🔴 Desconectado"}
      </div>

      {/* Usuarios online */}
      {onlineUsers.length > 0 && (
        <div className="online-users">
          Usuarios online: {onlineUsers.length}
        </div>
      )}

      {/* Indicador de typing */}
      {typingUsers.size > 0 && (
        <div className="typing-indicator">
          {Array.from(typingUsers).map((userId) => (
            <span key={userId}>Usuario {userId} está escribiendo...</span>
          ))}
        </div>
      )}

      {/* Mensajes */}
      <div className="messages">
        {messages.map((message) => (
          <div
            key={message.id}
            className={`message ${message.senderId === currentUserId ? "own" : "other"}`}
          >
            <div className="sender">{message.senderName || "Usuario eliminado"}</div>
            <div className="content">{message.content}</div>
            <div className="time">{new Date(message.sentAt).toLocaleTimeString()}</div>
            {message.attachmentUrls.length > 0 && (
              <div className="attachments">
                {message.attachmentUrls.map((url, idx) => (
                  <img key={idx} src={url} alt={`Attachment ${idx + 1}`} />
                ))}
              </div>
            )}
          </div>
        ))}
      </div>

      {/* Input de mensaje */}
      <input
        type="text"
        placeholder="Escribe un mensaje..."
        onInput={handleTyping}
        // ... resto de la lógica del input
      />
    </div>
  );
};

export default ChatComponent;
```

---

## Ejemplo Completo JavaScript Vanilla

```javascript
class ChatManager {
  constructor(apiUrl, conversationId, currentUserId) {
    this.apiUrl = apiUrl;
    this.conversationId = conversationId;
    this.currentUserId = currentUserId;
    this.connection = null;
    this.messages = [];
    this.isConnected = false;
    this.typingTimeout = null;
    this.typingUsers = new Set();
    this.onlineUsers = [];
  }

  async connect() {
    const token = localStorage.getItem("authToken");
    if (!token) {
      throw new Error("Token no encontrado");
    }

    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.apiUrl}/chatHub`, {
        accessTokenFactory: () => token,
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling,
        timeout: 30000
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          if (retryContext.previousRetryCount === 0) return 0;
          if (retryContext.previousRetryCount === 1) return 2000;
          if (retryContext.previousRetryCount === 2) return 5000;
          return Math.min(10000, retryContext.elapsedMilliseconds);
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    // Eventos de conexión
    this.connection.onreconnecting(() => {
      console.log("Reconectando...");
      this.isConnected = false;
      this.updateConnectionStatus();
    });

    this.connection.onreconnected((connectionId) => {
      console.log("Reconectado:", connectionId);
      this.isConnected = true;
      this.updateConnectionStatus();
      this.connection.invoke("JoinConversation", this.conversationId).catch(console.error);
    });

    this.connection.onclose((error) => {
      console.log("Conexión cerrada", error);
      this.isConnected = false;
      this.updateConnectionStatus();
    });

    // Eventos del chat
    this.connection.on("ReceiveMessage", (message) => {
      this.messages.push(message);
      this.renderMessages();
    });

    this.connection.on("MessageRead", (messageId) => {
      const message = this.messages.find((m) => m.id === messageId);
      if (message) {
        message.isRead = true;
        this.renderMessages();
      }
    });

    this.connection.on("UserTyping", (data) => {
      if (data.conversationId !== this.conversationId) return;
      
      if (data.isTyping) {
        this.typingUsers.add(data.userId);
      } else {
        this.typingUsers.delete(data.userId);
      }
      this.renderTypingIndicator();
    });

    this.connection.on("UserJoinedConversation", (data) => {
      if (data.conversationId === this.conversationId) {
        if (!this.onlineUsers.includes(data.userId)) {
          this.onlineUsers.push(data.userId);
        }
        this.renderOnlineUsers();
      }
    });

    this.connection.on("UserLeftConversation", (data) => {
      if (data.conversationId === this.conversationId) {
        this.onlineUsers = this.onlineUsers.filter((id) => id !== data.userId);
        this.typingUsers.delete(data.userId);
        this.renderOnlineUsers();
        this.renderTypingIndicator();
      }
    });

    this.connection.on("OnlineUsers", (data) => {
      if (data.conversationId === this.conversationId) {
        this.onlineUsers = data.userIds;
        this.renderOnlineUsers();
      }
    });

    // Iniciar conexión
    try {
      await this.connection.start();
      console.log("Conectado a SignalR");
      this.isConnected = true;
      this.updateConnectionStatus();
      
      await this.connection.invoke("JoinConversation", this.conversationId);
      await this.connection.invoke("GetOnlineUsers", this.conversationId);
    } catch (error) {
      console.error("Error al conectar:", error);
      throw error;
    }
  }

  async disconnect() {
    if (this.connection && this.isConnected) {
      await this.connection.invoke("LeaveConversation", this.conversationId);
      await this.connection.stop();
      this.isConnected = false;
      this.updateConnectionStatus();
    }
  }

  handleTyping() {
    if (!this.connection || !this.isConnected) return;

    if (!this.isTyping) {
      this.isTyping = true;
      this.connection.invoke("UserTyping", this.conversationId, true).catch(console.error);
    }

    if (this.typingTimeout) {
      clearTimeout(this.typingTimeout);
    }

    this.typingTimeout = setTimeout(() => {
      if (this.connection && this.isConnected) {
        this.isTyping = false;
        this.connection.invoke("UserTyping", this.conversationId, false).catch(console.error);
      }
    }, 3000);
  }

  updateConnectionStatus() {
    const statusEl = document.getElementById("connection-status");
    if (statusEl) {
      statusEl.textContent = this.isConnected ? "🟢 Conectado" : "🔴 Desconectado";
      statusEl.className = this.isConnected ? "connected" : "disconnected";
    }
  }

  renderMessages() {
    const messagesEl = document.getElementById("messages");
    if (!messagesEl) return;

    messagesEl.innerHTML = this.messages
      .map(
        (msg) => `
      <div class="message ${msg.senderId === this.currentUserId ? "own" : "other"}">
        <div class="sender">${msg.senderName || "Usuario eliminado"}</div>
        <div class="content">${msg.content || ""}</div>
        <div class="time">${new Date(msg.sentAt).toLocaleTimeString()}</div>
      </div>
    `
      )
      .join("");
  }

  renderTypingIndicator() {
    const typingEl = document.getElementById("typing-indicator");
    if (!typingEl) return;

    if (this.typingUsers.size > 0) {
      typingEl.textContent = Array.from(this.typingUsers)
        .map((userId) => `Usuario ${userId} está escribiendo...`)
        .join(", ");
      typingEl.style.display = "block";
    } else {
      typingEl.style.display = "none";
    }
  }

  renderOnlineUsers() {
    const onlineEl = document.getElementById("online-users");
    if (onlineEl) {
      onlineEl.textContent = `Usuarios online: ${this.onlineUsers.length}`;
    }
  }
}

// Uso
const chatManager = new ChatManager("http://localhost:7124", 123, 456);
chatManager.connect().catch(console.error);

// Cleanup al salir
window.addEventListener("beforeunload", () => {
  chatManager.disconnect();
});
```

---

## Mejores Prácticas 2025

### 1. **Reconexión Automática**
- ✅ Usa `withAutomaticReconnect()` con estrategia exponencial
- ✅ Re-join a conversaciones después de reconectar
- ✅ Muestra indicador visual del estado de conexión

### 2. **Manejo de Errores**
- ✅ Captura errores en todas las invocaciones
- ✅ Maneja errores de autenticación (401) redirigiendo a login
- ✅ Maneja errores de autorización (403) mostrando mensaje al usuario

### 3. **Performance**
- ✅ Limpia event listeners al desmontar componentes
- ✅ Usa debounce para eventos de typing (3 segundos)
- ✅ Limita el número de mensajes renderizados (paginación virtual)

### 4. **UX/UI**
- ✅ Muestra indicador de "escribiendo..." cuando otros usuarios escriben
- ✅ Muestra estado de conexión (conectado/desconectado/reconectando)
- ✅ Muestra usuarios online en la conversación
- ✅ Marca mensajes como leídos automáticamente cuando se ven

### 5. **Seguridad**
- ✅ Nunca hardcodees tokens en el código
- ✅ Renueva tokens antes de que expiren
- ✅ Maneja errores 401 redirigiendo a login

### 6. **Testing**
- ✅ Prueba reconexión simulando pérdida de conexión
- ✅ Prueba con múltiples usuarios en la misma conversación
- ✅ Prueba con tokens expirados

---

## Troubleshooting

### Problema: "Connection closed with an error"
**Solución:** Verifica que el token JWT sea válido y no haya expirado.

### Problema: "Failed to start connection"
**Solución:** 
- Verifica que la URL del hub sea correcta
- Verifica que CORS esté configurado correctamente
- Verifica que el servidor esté corriendo

### Problema: "Unauthorized"
**Solución:** 
- Verifica que el token esté en el formato correcto
- Verifica que el token no haya expirado
- Verifica que el token tenga los claims necesarios

### Problema: Mensajes no llegan
**Solución:**
- Verifica que hayas llamado `JoinConversation` antes de escuchar mensajes
- Verifica que estés escuchando el evento correcto (`ReceiveMessage`)
- Verifica la consola del navegador para errores

### Problema: Reconexión no funciona
**Solución:**
- Verifica que `withAutomaticReconnect()` esté configurado
- Verifica que no estés llamando `connection.stop()` manualmente
- Verifica los logs del servidor para errores

---

## Resumen de Endpoints REST

Además de SignalR, también necesitas usar estos endpoints REST:

### Enviar Mensaje
```
POST /api/Chat/message
Content-Type: multipart/form-data

{
  ConversationId: number,
  Content?: string,
  LocationLatitude?: string,
  LocationLongitude?: string,
  Attachments?: File[]
}
```

### Obtener Conversación
```
GET /api/Chat/conversation?searchId={searchId}
Authorization: Bearer {token}
```

### Marcar Mensaje como Leído
```
PUT /api/Chat/message/{messageId}/read
Authorization: Bearer {token}
```

---

## Conclusión

Con esta implementación tendrás un chat en vivo robusto y escalable usando las mejores prácticas de SignalR 2025. El backend ya está optimizado, solo necesitas implementar el cliente siguiendo esta guía.

**¿Preguntas?** Revisa los logs del servidor y del cliente para debugging detallado.
