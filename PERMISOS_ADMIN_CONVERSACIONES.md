# 👮 Permisos del Admin en Conversaciones

## 📋 Resumen

El **Admin puede VER todas las conversaciones** para soporte y moderación, pero **NO puede enviar mensajes**.

---

## ✅ Permisos del Admin

### **1. VER Conversaciones (✅ Permitido)**

El Admin puede acceder a todas las conversaciones en los siguientes endpoints:

#### **a) GetConversationById**
```csharp
// Línea 83-85
.Where(c => c.Id == conversationId &&
           ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
            (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
            _authService.IsAdmin(User)))  // ✅ Admin puede ver
```

#### **b) GetConversationBySearchServiceId (Pre-contratación)**
```csharp
// Línea 510-512
.Where(c => c.SearchServiceId == searchServiceId &&
           c.IsActive == true &&
           ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
            (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
            _authService.IsAdmin(User)))  // ✅ Admin puede ver
```

#### **c) GetConversationBySearchHireId (Post-contratación)**
```csharp
// Línea 664-667
.FirstOrDefaultAsync(c => c.SearchHireId == searchHireId &&
                         ((c.ClientId.HasValue && c.ClientId.Value == userId) || 
                          (c.ExpertId.HasValue && c.ExpertId.Value == userId) || 
                          _authService.IsAdmin(User)))  // ✅ Admin puede ver
```

#### **d) GetMyConversations**
```csharp
// Línea 1337
.Where(c => c.IsActive == true &&
           ((c.ClientId.HasValue && c.ClientId.Value == userId) ||
            (c.ExpertId.HasValue && c.ExpertId.Value == userId) ||
            _authService.IsAdmin(User)))  // ✅ Admin puede ver todas
```

#### **e) GetPreHireConversations**
```csharp
// Línea 175
.Where(c => c.IsActive == true &&
           c.SearchServiceId != null &&
           c.SearchHireId == null &&
           ((c.ExpertId.HasValue && c.ExpertId.Value == userId) ||
            _authService.IsAdmin(User)))  // ✅ Admin puede ver todas
```

---

### **2. ENVIAR Mensajes (❌ NO Permitido)**

El Admin **NO puede enviar mensajes** en conversaciones donde no es cliente ni experto:

```csharp
// Línea 817-830
var isAdmin = _authService.IsAdmin(User);
var conversation = await _context.Conversations
    .Include(c => c.Messages)
    .FirstOrDefaultAsync(c => c.Id == dto.ConversationId);

if (!UserBelongsToConversation(conversation, userId, isAdmin))
{
    return Unauthorized(new { message = "You are not authorized to send messages to this conversation" });
}

// ✅ VALIDACIÓN CRÍTICA: Asegurar que solo cliente y experto puedan enviar mensajes
var isClient = conversation.ClientId.HasValue && conversation.ClientId.Value == userId;
var isExpert = conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId;

if (!isClient && !isExpert)
{
    // ⚠️ CRÍTICO: Usuario no autorizado intentando enviar mensaje
    await _loggingService.LogErrorAsync(...);
    return Unauthorized(new { message = "Only the client and expert can send messages in this conversation" });
}
```

**Resultado:** El Admin puede ver la conversación, pero si intenta enviar un mensaje, será rechazado.

---

### **3. UserBelongsToConversation**

```csharp
// Línea 1435-1460
private bool UserBelongsToConversation(Conversation conversation, int userId, bool isAdmin)
{
    if (conversation == null)
    {
        return false;
    }

    // ✅ Solo cliente y experto pueden enviar mensajes
    // Los admins pueden VER conversaciones pero NO enviar mensajes
    
    if (conversation.ClientId.HasValue && conversation.ClientId.Value == userId)
    {
        return true;
    }

    if (conversation.ExpertId.HasValue && conversation.ExpertId.Value == userId)
    {
        return true;
    }

    // ⚠️ Si es admin, registrar warning pero NO permitir enviar mensajes
    if (isAdmin)
    {
        _loggingService.LogWarningAsync(...).Wait();
    }

    return false;  // ✅ Admin NO puede enviar mensajes
}
```

---

## 📊 Matriz de Permisos

| Acción | Cliente | Experto | Admin |
|--------|---------|---------|-------|
| **Ver conversación propia** | ✅ | ✅ | ✅ |
| **Ver conversación ajena** | ❌ | ❌ | ✅ |
| **Enviar mensaje en propia** | ✅ | ✅ | ❌ |
| **Enviar mensaje en ajena** | ❌ | ❌ | ❌ |
| **Crear conversación** | ✅ | ❌ | ✅ (solo post-contratación) |

---

## 🔍 Logging de Acceso de Admin

Cuando el Admin accede a una conversación pre-contratación, se registra un warning:

```csharp
// Línea 593-610
if (_authService.IsAdmin(User) && 
    conversation.ClientId.HasValue && conversation.ClientId.Value != userId && 
    conversation.ExpertId.HasValue && conversation.ExpertId.Value != userId)
{
    await _loggingService.LogWarningAsync(
        message: "Admin accessed pre-hire conversation",
        details: $"Admin {userId} accessed pre-hire conversation {conversation.Id} for SearchServiceId {searchServiceId}...",
        ...
    );
}
```

Esto permite rastrear cuándo y por qué el Admin accede a conversaciones.

---

## ✅ Resumen

### **Admin PUEDE:**
- ✅ Ver todas las conversaciones (pre y post-contratación)
- ✅ Acceder a conversaciones de cualquier cliente o experto
- ✅ Ver mensajes de todas las conversaciones
- ✅ Ver detalles completos de conversaciones

### **Admin NO PUEDE:**
- ❌ Enviar mensajes en conversaciones donde no es cliente ni experto
- ❌ Crear conversaciones pre-contratación (solo clientes pueden)
- ❌ Modificar conversaciones existentes

---

## 🎯 Casos de Uso del Admin

1. **Soporte al Cliente:** Ver conversaciones para ayudar a resolver problemas
2. **Moderación:** Revisar conversaciones para detectar comportamientos inapropiados
3. **Investigación:** Acceder a conversaciones para investigar disputas o problemas
4. **Auditoría:** Revisar conversaciones para cumplimiento y seguridad

---

**Fecha:** 2026-01-27  
**Estado:** ✅ Implementado correctamente
