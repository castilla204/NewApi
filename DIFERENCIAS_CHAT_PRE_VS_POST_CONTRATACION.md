# 🔄 Diferencias: Chat Pre-Contratación vs Chat Post-Contratación

## 📋 Resumen Ejecutivo

| Aspecto | Chat Pre-Contratación | Chat Post-Contratación |
|---------|----------------------|----------------------|
| **Cuándo se usa** | Antes de contratar un servicio | Después de contratar un servicio |
| **Ruta** | `/chat-pre-contratacion/{searchServiceId}` | `/chat/{searchHireId}` o `/searchhire/{id}` |
| **Endpoint chat** | `GET /api/Chat/conversation-by-service?searchServiceId={id}` | `GET /api/Chat/by-searchhire/{searchHireId}` |
| **Endpoint detalles** | ❌ No tiene | ✅ `GET /api/searchhire/{id}/details-complete` |
| **Componente** | `PreHireChat` (solo chat) | `PostHireChat` + `DetailsTab` (chat + detalles) |
| **Información mostrada** | Solo chat + info básica del servicio | Chat + información completa de la contratación |
| **Tabs/Vistas** | ❌ No tiene tabs | ✅ Tiene tabs: Chat | Detalles |

---

## 🎯 1. CHAT PRE-CONTRATACIÓN

### **Cuándo se usa:**
- El cliente quiere hablar con el experto **antes de contratar** el servicio
- El cliente está viendo un servicio y quiere hacer preguntas
- El cliente aún no ha realizado el pago

### **Ruta/Navegación:**
```typescript
// Desde la lista de conversaciones
router.push(`/chat-pre-contratacion/${conversation.searchServiceId}`);

// Desde la página de detalles del servicio
router.push(`/chat-pre-contratacion/${serviceId}`);
```

### **Endpoint que se llama:**
```typescript
// ✅ Endpoint para obtener/crear conversación
GET /api/Chat/conversation-by-service?searchServiceId={id}

// Respuesta:
{
  "id": 123,
  "searchHireId": null,        // ✅ Siempre null
  "searchServiceId": 456,       // ✅ ID del servicio
  "clientId": 1,
  "expertId": 2,
  "messages": [...]
}
```

### **Componente Frontend:**
```typescript
// Componente: PreHireChat.tsx
<PreHireChat
  serviceId={searchServiceId}
  token={token}
  userId={userId}
  onClose={() => setShowChat(false)}
/>
```

### **Estructura de la Página:**
```
┌─────────────────────────────────────┐
│  Información del Servicio            │
│  - Nombre del servicio               │
│  - Precio                            │
│  - Experto                           │
│  - Imágenes                          │
├─────────────────────────────────────┤
│  💬 Chat Pre-Contratación            │
│  ┌───────────────────────────────┐  │
│  │ Header: Nombre del experto     │  │
│  ├───────────────────────────────┤  │
│  │ Mensajes                      │  │
│  │ - Mensaje 1                   │  │
│  │ - Mensaje 2                   │  │
│  ├───────────────────────────────┤  │
│  │ Input: Escribe tu mensaje...   │  │
│  └───────────────────────────────┘  │
├─────────────────────────────────────┤
│  [Contratar Servicio]                │
└─────────────────────────────────────┘
```

### **Información Mostrada:**
- ✅ **Chat:** Mensajes entre cliente y experto
- ✅ **Info básica del servicio:** Nombre, precio, experto
- ❌ **NO tiene:** Estado de contratación, entregables, citas, disputas, etc.

### **Funcionalidades Disponibles:**
- ✅ Enviar mensajes
- ✅ Recibir mensajes en tiempo real (Supabase Realtime)
- ✅ Ver historial de mensajes
- ✅ Botón para contratar el servicio
- ❌ **NO tiene:** Gestión de entregables, citas, disputas, etc.

---

## 🎯 2. CHAT POST-CONTRATACIÓN (Con Tab Detalles)

### **Cuándo se usa:**
- El cliente **ya contrató** el servicio
- El cliente quiere ver el estado de su contratación
- El cliente necesita gestionar entregables, citas, etc.

### **Ruta/Navegación:**
```typescript
// Desde la lista de conversaciones
router.push(`/chat/${conversation.searchHireId}`);
// O mejor:
router.push(`/searchhire/${conversation.searchHireId}`);

// Desde la lista de contrataciones
router.push(`/searchhire/${searchHireId}`);
```

### **Endpoints que se llaman:**
```typescript
// ✅ 1. Endpoint para obtener conversación
GET /api/Chat/by-searchhire/{searchHireId}

// Respuesta:
{
  "id": 124,
  "searchHireId": 789,          // ✅ ID de la contratación
  "searchServiceId": null,       // ✅ null (o ID del servicio)
  "clientId": 1,
  "expertId": 2,
  "messages": [...]
}

// ✅ 2. Endpoint para obtener detalles completos (TAB DETALLES)
GET /api/searchhire/{id}/details-complete

// Respuesta:
{
  "search": {
    "id": 123,
    "title": "Necesito clases de inglés",
    "description": "...",
    "searchHire": {
      "id": 789,
      "status": "InProgress",
      "amount": 75.50,
      "expert": {...},
      "service": {...}
    }
  },
  "moneyDistribution": {...},
  "category": {...},
  "review": {...},
  "appointment": {...},          // ✅ Cita si existe
  "deliverables": [...],         // ✅ Archivos entregados
  "disputes": [...],             // ✅ Disputas si existen
  "requiredDeliverableTypes": [...],
  "expertProfile": {...}
}
```

### **Componente Frontend:**
```typescript
// Componente: PostHireChatWithDetails.tsx
<PostHireChatWithDetails
  searchHireId={searchHireId}
  token={token}
  userId={userId}
/>

// Estructura interna:
<div className="post-hire-chat-container">
  <Tabs>
    <Tab label="Chat">
      <PostHireChat searchHireId={searchHireId} />
    </Tab>
    <Tab label="Detalles">
      <DetailsTab searchHireId={searchHireId} />
    </Tab>
  </Tabs>
</div>
```

### **Estructura de la Página:**
```
┌─────────────────────────────────────────────────┐
│  Header: Información de la Contratación         │
│  - Estado: "En Progreso"                        │
│  - Monto: 75.50€                               │
│  - Experto: Carlos García                       │
├─────────────────────────────────────────────────┤
│  [Chat] [Detalles]  ← TABS                      │
├─────────────────────────────────────────────────┤
│                                                  │
│  TAB CHAT:                                       │
│  ┌──────────────────────────────────────────┐  │
│  │ Mensajes                                 │  │
│  │ - Mensaje 1                              │  │
│  │ - Mensaje 2                              │  │
│  │ - Mensaje 3                              │  │
│  ├──────────────────────────────────────────┤  │
│  │ Input: Escribe tu mensaje...             │  │
│  └──────────────────────────────────────────┘  │
│                                                  │
│  TAB DETALLES:                                   │
│  ┌──────────────────────────────────────────┐  │
│  │ 📋 Información de la Búsqueda            │  │
│  │ - Título: "Necesito clases de inglés"    │  │
│  │ - Descripción: "..."                     │  │
│  ├──────────────────────────────────────────┤  │
│  │ 💰 Distribución de Dinero               │  │
│  │ - Cliente: 60%                           │  │
│  │ - Experto: 35%                           │  │
│  │ - Plataforma: 5%                         │  │
│  ├──────────────────────────────────────────┤  │
│  │ 📎 Entregables                           │  │
│  │ - [Archivo 1] [Descargar]                │  │
│  │ - [Archivo 2] [Descargar]                │  │
│  ├──────────────────────────────────────────┤  │
│  │ 📅 Cita (si aplica)                      │  │
│  │ - Fecha: 2026-01-30                      │  │
│  │ - Hora: 10:00                            │  │
│  │ - Estado: Confirmada                      │  │
│  ├──────────────────────────────────────────┤  │
│  │ ⚠️ Disputas (si existen)                 │  │
│  │ - Disputa #1: [Ver detalles]             │  │
│  ├──────────────────────────────────────────┤  │
│  │ ⭐ Reseña (si existe)                     │  │
│  │ - Calificación: 5/5                      │  │
│  │ - Comentario: "Excelente servicio"       │  │
│  └──────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
```

### **Información Mostrada:**

#### **Tab Chat:**
- ✅ Mensajes entre cliente y experto
- ✅ Historial completo (incluye mensajes pre-contratación si existían)

#### **Tab Detalles:**
- ✅ **Información de la búsqueda:** Título, descripción
- ✅ **Estado de la contratación:** InProgress, Completed, etc.
- ✅ **Distribución de dinero:** Porcentajes y montos
- ✅ **Entregables:** Archivos que el experto ha entregado
- ✅ **Citas:** Si el servicio requiere cita, muestra fecha/hora/estado
- ✅ **Disputas:** Si hay problemas, muestra las disputas
- ✅ **Reseña:** Si ya se completó y se dejó reseña
- ✅ **Perfil del experto:** Información completa del experto
- ✅ **Tipos de entregables requeridos:** Qué archivos debe entregar el experto

### **Funcionalidades Disponibles:**

#### **Tab Chat:**
- ✅ Enviar mensajes
- ✅ Recibir mensajes en tiempo real
- ✅ Ver historial completo
- ✅ Subir archivos adjuntos

#### **Tab Detalles:**
- ✅ Ver estado de la contratación
- ✅ Descargar entregables
- ✅ Ver/editar citas (si aplica)
- ✅ Crear/ver disputas
- ✅ Dejar reseña (cuando esté completado)
- ✅ Ver información financiera
- ✅ Ver perfil completo del experto

---

## 🔄 Flujo Completo: De Pre a Post Contratación

### **1. Cliente ve servicio y chatea (Pre-Contratación)**
```
Usuario → Página de Servicio
       → Clic en "Chatear antes de contratar"
       → GET /api/Chat/conversation-by-service?searchServiceId=456
       → Abre PreHireChat
       → Envía mensajes
       → Mensajes se guardan en conversación con searchServiceId=456
```

### **2. Cliente contrata el servicio**
```
Usuario → Clic en "Contratar servicio"
       → POST /api/SearchHire
       → Backend:
          - Crea SearchHire (id: 789)
          - Busca conversación previa (searchServiceId=456)
          - Migra mensajes a nueva conversación (searchHireId=789)
          - Marca conversación previa como inactiva
```

### **3. Cliente ve chat de contratación (Post-Contratación)**
```
Usuario → Lista de conversaciones
       → Clic en conversación post-contratación
       → GET /api/Chat/by-searchhire/789
       → GET /api/searchhire/789/details-complete
       → Abre PostHireChatWithDetails
       → Ve TODOS los mensajes (previos + nuevos)
       → Ve tab Detalles con toda la información
```

---

## 📊 Comparación Visual

### **Pre-Contratación:**
```
┌─────────────────────────────┐
│  Servicio: Reparación PC     │
│  Precio: 50€                │
│  Experto: Carlos García     │
├─────────────────────────────┤
│  💬 Chat                     │
│  ┌───────────────────────┐ │
│  │ Hola, ¿disponible?    │ │
│  │ Sí, claro             │ │
│  │ Perfecto               │ │
│  └───────────────────────┘ │
│  [Contratar]                │
└─────────────────────────────┘
```

### **Post-Contratación:**
```
┌─────────────────────────────────────┐
│  Estado: En Progreso | 75.50€       │
├─────────────────────────────────────┤
│  [Chat] [Detalles]                   │
├─────────────────────────────────────┤
│  TAB CHAT:                           │
│  ┌───────────────────────────────┐  │
│  │ Hola, ¿disponible?           │  │ ← Mensajes previos
│  │ Sí, claro                    │  │
│  │ Perfecto                      │  │
│  │ Te envío el archivo           │  │ ← Mensajes nuevos
│  └───────────────────────────────┘  │
│                                      │
│  TAB DETALLES:                       │
│  ┌───────────────────────────────┐  │
│  │ 📋 Búsqueda                   │  │
│  │ 💰 Dinero                      │  │
│  │ 📎 Entregables (2)            │  │
│  │ 📅 Cita: 30/01 10:00          │  │
│  │ ⭐ Reseña                     │  │
│  └───────────────────────────────┘  │
└─────────────────────────────────────┘
```

---

## 💻 Ejemplo de Código Frontend

### **Pre-Contratación:**
```typescript
// pages/ChatPreContratacion.tsx
const ChatPreContratacionPage = () => {
  const { searchServiceId } = useParams();
  
  // ✅ Solo un endpoint para el chat
  const { data: conversation } = useQuery({
    queryKey: ['pre-hire-conversation', searchServiceId],
    queryFn: () => fetch(
      `/api/Chat/conversation-by-service?searchServiceId=${searchServiceId}`,
      { headers: { 'Authorization': `Bearer ${token}` } }
    ).then(r => r.json())
  });

  return (
    <div>
      <ServiceInfo serviceId={searchServiceId} />
      <PreHireChat 
        conversation={conversation}
        serviceId={searchServiceId}
      />
      <button onClick={handleHire}>Contratar Servicio</button>
    </div>
  );
};
```

### **Post-Contratación:**
```typescript
// pages/ChatPostContratacion.tsx
const ChatPostContratacionPage = () => {
  const { searchHireId } = useParams();
  const [activeTab, setActiveTab] = useState<'chat' | 'details'>('chat');
  
  // ✅ Dos endpoints: uno para chat, otro para detalles
  const { data: conversation } = useQuery({
    queryKey: ['post-hire-conversation', searchHireId],
    queryFn: () => fetch(
      `/api/Chat/by-searchhire/${searchHireId}`,
      { headers: { 'Authorization': `Bearer ${token}` } }
    ).then(r => r.json())
  });

  const { data: details } = useQuery({
    queryKey: ['searchhire-details', searchHireId],
    queryFn: () => fetch(
      `/api/searchhire/${searchHireId}/details-complete`,
      { headers: { 'Authorization': `Bearer ${token}` } }
    ).then(r => r.json())
  });

  return (
    <div>
      <Header searchHire={details?.search?.searchHire} />
      
      <Tabs value={activeTab} onChange={setActiveTab}>
        <Tab value="chat" label="Chat">
          <PostHireChat conversation={conversation} />
        </Tab>
        <Tab value="details" label="Detalles">
          <DetailsTab details={details} />
        </Tab>
      </Tabs>
    </div>
  );
};
```

---

## 🎯 Resumen de Diferencias Clave

| Característica | Pre-Contratación | Post-Contratación |
|---------------|------------------|-------------------|
| **Ruta** | `/chat-pre-contratacion/{serviceId}` | `/chat/{hireId}` o `/searchhire/{id}` |
| **Endpoints** | 1 endpoint (chat) | 2 endpoints (chat + detalles) |
| **Tabs** | ❌ No tiene | ✅ Chat \| Detalles |
| **Información** | Solo chat + servicio básico | Chat + información completa |
| **Entregables** | ❌ No | ✅ Sí (tab Detalles) |
| **Citas** | ❌ No | ✅ Sí (tab Detalles) |
| **Disputas** | ❌ No | ✅ Sí (tab Detalles) |
| **Estado** | ❌ No | ✅ Sí (tab Detalles) |
| **Reseña** | ❌ No | ✅ Sí (tab Detalles) |
| **Botón contratar** | ✅ Sí | ❌ No (ya contratado) |

---

## ✅ Checklist para el Frontend

### **Pre-Contratación:**
- [ ] Ruta: `/chat-pre-contratacion/{searchServiceId}`
- [ ] Endpoint: `GET /api/Chat/conversation-by-service?searchServiceId={id}`
- [ ] Componente: `PreHireChat`
- [ ] Mostrar: Chat + info básica del servicio
- [ ] Botón: "Contratar Servicio"

### **Post-Contratación:**
- [ ] Ruta: `/chat/{searchHireId}` o `/searchhire/{id}`
- [ ] Endpoint chat: `GET /api/Chat/by-searchhire/{searchHireId}`
- [ ] Endpoint detalles: `GET /api/searchhire/{id}/details-complete`
- [ ] Componente: `PostHireChatWithDetails` con tabs
- [ ] Tab Chat: `PostHireChat`
- [ ] Tab Detalles: `DetailsTab` con toda la información
- [ ] Mostrar: Chat + información completa de la contratación

---

**Fecha:** 2026-01-27  
**Estado:** ✅ Documentación completa
