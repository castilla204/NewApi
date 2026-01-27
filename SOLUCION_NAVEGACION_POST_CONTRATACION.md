# 🔧 Solución: Navegación Post-Contratación

## 🐛 Problema Identificado

Cuando el usuario hace clic en una conversación **post-contratación**, el frontend está:

1. ❌ Navegando a `/busquedas/92` (ruta incorrecta - usa `searchId` en lugar de `searchHireId`)
2. ❌ Llamando al endpoint antiguo `/api/Search/{searchId}/details-complete` (ya no existe)
3. ❌ No está usando el `searchHireId` correcto

**Error mostrado:**
```
"searchHireId is required. The endpoint /api/Search/{searchId}/details-complete 
has been removed. Please use /api/searchhire/{id}/details-complete instead."
```

---

## ✅ Solución

### **1. Cambiar la Navegación en el Frontend**

Cuando el usuario hace clic en una conversación **post-contratación**, debe navegar a:

```typescript
// ❌ INCORRECTO (actual)
router.push(`/busquedas/${searchId}`);  // Usa searchId

// ✅ CORRECTO
router.push(`/searchhire/${conversation.searchHireId}`);  // Usa searchHireId
```

### **2. Cambiar el Endpoint que se Llama**

```typescript
// ❌ INCORRECTO (endpoint antiguo - ya no existe)
GET /api/Search/{searchId}/details-complete

// ✅ CORRECTO (endpoint nuevo)
GET /api/searchhire/{searchHireId}/details-complete
```

---

## 📝 Código Frontend a Corregir

### **En el Componente de Lista de Conversaciones:**

```typescript
// components/ConversationCard.tsx
const ConversationCard = ({ conversation }: { conversation: ClientConversationSummaryDto }) => {
  const router = useRouter();
  const isPreHire = conversation.conversationType === "pre-hire";
  
  const handleClick = () => {
    if (isPreHire) {
      // ✅ Pre-contratación: navegar a chat pre-contratación
      router.push(`/chat-pre-contratacion/${conversation.searchServiceId}`);
    } else {
      // ✅ Post-contratación: navegar a searchhire con searchHireId
      if (!conversation.searchHireId) {
        console.error('SearchHireId is missing for post-hire conversation');
        return;
      }
      router.push(`/searchhire/${conversation.searchHireId}`);
      // O alternativamente: router.push(`/chat/${conversation.searchHireId}`);
    }
  };
  
  return (
    <div className="conversation-card" onClick={handleClick}>
      {/* ... resto del componente ... */}
    </div>
  );
};
```

### **En la Página de Detalles de SearchHire:**

```typescript
// pages/SearchHireDetails.tsx o pages/Busquedas.tsx
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';

const SearchHireDetailsPage = () => {
  // ✅ CORRECTO: Obtener searchHireId de los parámetros
  const { id: searchHireId } = useParams<{ id: string }>();
  
  // ✅ CORRECTO: Llamar al endpoint nuevo con searchHireId
  const { data: details, isLoading, error } = useQuery({
    queryKey: ['searchhire-details', searchHireId],
    queryFn: async () => {
      if (!searchHireId) {
        throw new Error('searchHireId is required');
      }
      
      const response = await fetch(
        `/api/searchhire/${searchHireId}/details-complete`,
        {
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          }
        }
      );
      
      if (!response.ok) {
        throw new Error(`Error: ${response.status}`);
      }
      
      return response.json();
    },
    enabled: !!searchHireId
  });
  
  // ✅ CORRECTO: También obtener la conversación
  const { data: conversation } = useQuery({
    queryKey: ['post-hire-conversation', searchHireId],
    queryFn: async () => {
      const response = await fetch(
        `/api/Chat/by-searchhire/${searchHireId}`,
        {
          headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
          }
        }
      );
      
      if (!response.ok) {
        throw new Error(`Error: ${response.status}`);
      }
      
      return response.json();
    },
    enabled: !!searchHireId
  });
  
  if (isLoading) return <div>Cargando...</div>;
  
  if (error) {
    return (
      <div>
        <p>Error al cargar</p>
        <p>{error.message}</p>
        <button onClick={() => window.location.reload()}>Reintentar</button>
      </div>
    );
  }
  
  return (
    <div>
      <Header searchHire={details?.search?.searchHire} />
      
      <Tabs>
        <Tab label="Chat">
          <PostHireChat conversation={conversation} />
        </Tab>
        <Tab label="Detalles">
          <DetailsTab details={details} />
        </Tab>
      </Tabs>
    </div>
  );
};
```

---

## 🔍 Verificación de la Base de Datos

Para el SearchHire 92, verificar:

```sql
SELECT 
    sh."Id" as search_hire_id,
    sh."SearchId",              -- Este es el searchId (puede ser null)
    sh."SearchServiceId",
    sh."ClientId",
    sh."ExpertId"
FROM "SearchHires" sh
WHERE sh."Id" = 92;
```

**Importante:**
- Para navegar a la página de detalles, usar **`searchHireId`** (92)
- **NO usar** `searchId` (puede ser null o diferente)
- El endpoint correcto es: `/api/searchhire/92/details-complete`

---

## 📋 Checklist de Corrección

### **Frontend - Lista de Conversaciones:**
- [ ] Verificar que `conversation.searchHireId` existe para post-contratación
- [ ] Cambiar navegación de `/busquedas/{searchId}` a `/searchhire/{searchHireId}`
- [ ] Agregar validación: si `searchHireId` es null, mostrar error

### **Frontend - Página de Detalles:**
- [ ] Cambiar ruta de `/busquedas/:id` a `/searchhire/:id`
- [ ] Cambiar endpoint de `/api/Search/{id}/details-complete` a `/api/searchhire/{id}/details-complete`
- [ ] Verificar que el parámetro se llama `searchHireId` (no `searchId`)
- [ ] Agregar manejo de errores si `searchHireId` es null

### **Backend:**
- ✅ Endpoint `/api/searchhire/{id}/details-complete` ya existe y funciona
- ✅ Endpoint `/api/Chat/by-searchhire/{searchHireId}` ya existe y funciona

---

## 🎯 Resumen

| Aspecto | ❌ Incorrecto (Actual) | ✅ Correcto |
|---------|----------------------|------------|
| **Ruta** | `/busquedas/92` | `/searchhire/92` |
| **Parámetro** | `searchId` | `searchHireId` |
| **Endpoint detalles** | `/api/Search/{searchId}/details-complete` | `/api/searchhire/{searchHireId}/details-complete` |
| **Endpoint chat** | `/api/Chat/conversation?searchId=92` | `/api/Chat/by-searchhire/92` |

---

**Fecha:** 2026-01-27  
**Estado:** ⚠️ Requiere corrección en el frontend
