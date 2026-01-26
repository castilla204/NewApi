# 💬 Guía: Chat Pre-Contratación

## 📋 Resumen

Se ha implementado una nueva funcionalidad que permite a los usuarios chatear con el experto **antes de contratar** un servicio. Los mensajes enviados en este chat previo se migran automáticamente al chat del servicio contratado cuando se realiza la contratación.

---

## ✅ Cambios Implementados

### 1. **Modelo de Base de Datos**

#### `Conversation.cs`
- ✅ `SearchHireId` ahora es **nullable** (permite conversaciones sin SearchHire)
- ✅ Agregado `SearchServiceId` **nullable** (para conversaciones previas)
- ✅ Agregada relación de navegación `SearchService`

#### Migración EF Core
Archivo: `Migrations/20260126195342_AddSearchServiceIdToConversations.cs`

**✅ Migración creada con Entity Framework Core**

La migración incluye:
- ✅ Hacer `SearchHireId` nullable
- ✅ Agregar `SearchServiceId` nullable
- ✅ Crear índice `IX_Conversations_SearchServiceId`
- ✅ Crear índice `IX_Conversations_SearchHireId`
- ✅ Agregar foreign key `FK_Conversations_SearchServices_SearchServiceId`

**Para aplicar la migración:**

```powershell
# Desde el directorio del proyecto
cd "C:\Users\Diego\Downloads\App\App\NewApi"
dotnet ef database update
```

O aplicar solo esta migración específica:
```powershell
dotnet ef database update AddSearchServiceIdToConversations
```

**⚠️ IMPORTANTE:** Esta migración debe aplicarse en la **base de datos principal** (no en Supabase).

---

### 2. **Backend - Nuevos Endpoints**

#### `GET /api/Chat/conversation-by-service?searchServiceId={id}`

**Descripción:** Obtiene o crea una conversación previa a contratar para un servicio específico.

**Parámetros:**
- `searchServiceId` (query): ID del servicio

**Respuesta:**
```json
{
  "id": 1,
  "searchHireId": null,
  "searchServiceId": 123,
  "clientId": 1,
  "expertId": 2,
  "isActive": true,
  "createdAt": "2026-01-XX...",
  "updatedAt": "2026-01-XX...",
  "messages": [...]
}
```

**Comportamiento:**
- Si existe una conversación previa para ese servicio y usuario, la devuelve
- Si no existe, crea una nueva conversación previa
- El experto del servicio no puede chatear consigo mismo (retorna error)

---

### 3. **Migración Automática de Mensajes**

Cuando se contrata un servicio (`POST /api/SearchHire`):

1. ✅ Busca si existe una conversación previa con `SearchServiceId` y los mismos `ClientId` y `ExpertId`
2. ✅ Si existe y tiene mensajes:
   - Crea una nueva conversación vinculada al `SearchHire`
   - **Migra todos los mensajes** (incluyendo attachments) a la nueva conversación
   - Marca la conversación previa como `IsActive = false`
   - Mantiene la fecha original de creación de la conversación
3. ✅ Si no existe conversación previa, crea una nueva conversación vacía

**Resultado:** Los mensajes previos aparecen en el chat del servicio contratado.

---

### 4. **DTOs Actualizados**

#### `ConversationDto`
```csharp
public class ConversationDto
{
    public int Id { get; set; }
    public int? SearchHireId { get; set; } // ✅ Nullable
    public int? SearchServiceId { get; set; } // ✅ NUEVO
    public int? ClientId { get; set; }
    public int? ExpertId { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<MessageDto> Messages { get; set; }
}
```

---

## 🎨 Frontend - Implementación

### Componente de Chat Pre-Contratación

El frontend debe implementar un componente de chat simplificado para la página de detalles del servicio.

#### Ejemplo de uso:

```typescript
// En la página de detalles del servicio
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';

const ServiceDetailPage = ({ serviceId }: { serviceId: number }) => {
  const [showChat, setShowChat] = useState(false);
  const { data: conversation } = useQuery(
    ['pre-hire-conversation', serviceId],
    () => fetch(`/api/Chat/conversation-by-service?searchServiceId=${serviceId}`, {
      headers: { 'Authorization': `Bearer ${token}` }
    }).then(res => res.json()),
    { enabled: showChat }
  );

  return (
    <div>
      {/* Información del servicio */}
      <ServiceInfo service={service} />
      
      {/* Botón para abrir chat */}
      <button onClick={() => setShowChat(!showChat)}>
        💬 Chatear antes de contratar
      </button>
      
      {/* Chat simplificado */}
      {showChat && conversation && (
        <PreHireChat 
          conversationId={conversation.id}
          serviceId={serviceId}
          token={token}
        />
      )}
    </div>
  );
};
```

#### Componente `PreHireChat`

```typescript
interface PreHireChatProps {
  conversationId: number;
  serviceId: number;
  token: string;
}

const PreHireChat = ({ conversationId, serviceId, token }: PreHireChatProps) => {
  // Usar los mismos hooks de Supabase Realtime que el chat normal
  const messages = useConversationMessages(conversationId);
  const [inputValue, setInputValue] = useState('');
  
  const sendMessage = async () => {
    const formData = new FormData();
    formData.append('ConversationId', conversationId.toString());
    formData.append('Content', inputValue);
    
    await fetch(`${API_URL}/api/Chat/message`, {
      method: 'POST',
      headers: { 'Authorization': `Bearer ${token}` },
      body: formData
    });
    
    setInputValue('');
  };

  return (
    <div className="pre-hire-chat">
      <div className="chat-header">
        <h3>Chat antes de contratar</h3>
      </div>
      
      <div className="messages">
        {messages.map(msg => (
          <MessageBubble 
            key={msg.id}
            message={msg}
            showProfilePicture={true} // ✅ Mantener foto de perfil
          />
        ))}
      </div>
      
      <div className="chat-input">
        <input
          value={inputValue}
          onChange={(e) => setInputValue(e.target.value)}
          onKeyPress={(e) => e.key === 'Enter' && sendMessage()}
          placeholder="Escribe tu mensaje..."
        />
        <button onClick={sendMessage}>Enviar</button>
      </div>
    </div>
  );
};
```

---

## 🔄 Flujo Completo

### 1. Usuario ve servicio
```
GET /api/SearchService/{id}
→ Muestra información del servicio
```

### 2. Usuario hace clic en "Chatear"
```
GET /api/Chat/conversation-by-service?searchServiceId={id}
→ Crea/obtiene conversación previa
→ Abre chat simplificado
```

### 3. Usuario envía mensajes
```
POST /api/Chat/message
→ Mensajes se guardan en conversación previa
→ Supabase Realtime notifica en tiempo real
```

### 4. Usuario contrata el servicio
```
POST /api/SearchHire
→ Busca conversación previa
→ Migra mensajes a conversación de SearchHire
→ Marca conversación previa como inactiva
```

### 5. Usuario ve chat del servicio contratado
```
GET /api/Chat/by-searchhire/{searchHireId}
→ Muestra TODOS los mensajes (previos + nuevos)
```

---

## ✅ Características

- ✅ **Tiempo real:** Usa Supabase Realtime (igual que el chat normal)
- ✅ **Migración automática:** Los mensajes previos aparecen en el chat contratado
- ✅ **Foto de perfil:** Se mantiene en el chat simplificado
- ✅ **Simplificado:** No incluye toda la funcionalidad del chat completo (deliverables, disputas, etc.)
- ✅ **Validación:** El experto no puede chatear consigo mismo

---

## 📝 Notas Importantes

1. **Migración de Base de Datos:** Aplicar `migration_add_searchserviceid_conversations.sql` en la base de datos principal antes de desplegar.

2. **Supabase Realtime:** Funciona igual que el chat normal - los mensajes se notifican automáticamente vía `postgres_changes`.

3. **Conversaciones Previas:** Se marcan como `IsActive = false` después de la migración, pero se mantienen en la BD para historial.

4. **Seguridad:** Solo el cliente y el experto pueden acceder a su conversación previa.

---

## 🧪 Testing

### Casos de prueba:

1. ✅ Crear conversación previa para un servicio
2. ✅ Enviar mensajes en conversación previa
3. ✅ Contratar servicio y verificar migración de mensajes
4. ✅ Verificar que mensajes previos aparecen en chat contratado
5. ✅ Verificar que experto no puede chatear consigo mismo
6. ✅ Verificar tiempo real con Supabase Realtime

---

## 📚 Referencias

- `Controllers/ChatController.cs` - Endpoint `GetConversationBySearchServiceId`
- `Controllers/SearchHireController.cs` - Migración de mensajes en `CreateSearchHire`
- `DataLayer/Models/PostGresModels/Conversation.cs` - Modelo actualizado
- `migration_add_searchserviceid_conversations.sql` - Migración SQL

---

**Fecha de implementación:** 2026-01-XX  
**Estado:** ✅ Completado
