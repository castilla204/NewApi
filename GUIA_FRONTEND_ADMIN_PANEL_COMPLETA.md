# 🎯 GUÍA FRONTEND COMPLETA - ADMIN PANEL: TODOS LOS ENDPOINTS Y DTOs

## ⚠️ IMPORTANTE: LEE ESTO PRIMERO

Esta guía contiene **TODOS** los endpoints del admin panel con:
- ✅ **DTOs exactos** de request y response
- ✅ **Ejemplos reales** de uso
- ✅ **Errores comunes** y cómo evitarlos
- ✅ **Validaciones** requeridas

**TODOS los endpoints requieren:**
- Header: `Authorization: Bearer {accessToken}`
- El token debe tener rol `Admin` en el claim `Role`

---

## 📋 ÍNDICE COMPLETO

### **GESTIÓN DE USUARIOS**
1. [GET /api/User/all - Listar usuarios](#1-get-apiuserall)
2. [PUT /api/User/{userId}/block - Bloquear/Desbloquear usuario](#2-put-apiuseruseridblock)
3. [DELETE /api/User/{userId} - Eliminar usuario](#3-delete-apiuseruserid)

### **GESTIÓN DE DISPUTAS**
4. [GET /api/Dispute/all - Listar disputas](#4-get-apidisputeall)
5. [PUT /api/Dispute/{disputeId}/resolve - Resolver disputa](#5-put-apidisputedisputeidresolve)
6. [GET /api/Dispute/{disputeId} - Obtener disputa](#6-get-apidisputedisputeid)

### **GESTIÓN DE LOGS**
7. [GET /api/Log/critical - Logs críticos](#7-get-apilogcritical)
8. [GET /api/Log/types - Tipos de logs](#8-get-apilogtypes)

### **GESTIÓN DE ESTADOS DEL SISTEMA**
9. [GET /api/SystemStatus/statuses - Listar estados](#9-get-apisystemstatusstatuses)
10. [POST /api/SystemStatus/statuses - Crear estado](#10-post-apisystemstatusstatuses)
11. [PUT /api/SystemStatus/statuses/{statusId} - Actualizar estado](#11-put-apisystemstatusstatusesstatusid)
12. [DELETE /api/SystemStatus/statuses/{statusId} - Eliminar estado](#12-delete-apisystemstatusstatusesstatusid)
13. [GET /api/SystemStatus/mappings - Mapeos de estados](#13-get-apisystemstatusmappings)
14. [POST /api/SystemStatus/mappings - Crear mapeo](#14-post-apisystemstatusmappings)
15. [PUT /api/SystemStatus/mappings/{mappingId} - Actualizar mapeo](#15-put-apisystemstatusmappingsmappingid)
16. [DELETE /api/SystemStatus/mappings/{mappingId} - Eliminar mapeo](#16-delete-apisystemstatusmappingsmappingid)
17. [GET /api/SystemStatus/configurations - Configuraciones de distribución](#17-get-apisystemstatusconfigurations)
18. [POST /api/SystemStatus/configurations - Crear configuración](#18-post-apisystemstatusconfigurations)

### **GESTIÓN DE CATEGORÍAS**
19. [POST /api/Categories - Crear categoría](#19-post-apicategories)
20. [POST /api/Categories/fix-sequence - Corregir secuencia](#20-post-apicategoriesfix-sequence)

### **GESTIÓN DE TIPOS DE SERVICIO**
21. [POST /api/ServiceType - Crear tipo de servicio](#21-post-apiservicetype)
22. [PUT /api/ServiceType/{id} - Actualizar tipo de servicio](#22-put-apiservicetypeid)
23. [DELETE /api/ServiceType/{id} - Eliminar tipo de servicio](#23-delete-apiservicetypeid)

### **GESTIÓN DE CATEGORÍAS DE TIPOS DE SERVICIO**
24. [POST /api/ServiceTypeCategory - Crear categoría de tipo](#24-post-apiservicetypecategory)
25. [PUT /api/ServiceTypeCategory/{id} - Actualizar categoría de tipo](#25-put-apiservicetypecategoryid)
26. [DELETE /api/ServiceTypeCategory/{id} - Eliminar categoría de tipo](#26-delete-apiservicetypecategoryid)

### **CONFIGURACIÓN DE CITAS**
27. [GET /api/AppointmentConfig/appointment-status-configs - Configuraciones de estados](#27-get-apiappointmentconfigappointment-status-configs)

### **ADMINISTRACIÓN GENERAL**
28. [GET /api/Admin/suspicious-users - Usuarios sospechosos](#28-get-apiadminsuspicious-users)
29. [POST /api/Admin/block-user/{userId} - Bloquear usuario sospechoso](#29-post-apiadminblock-useruserid)
30. [GET /api/Admin/stripe/mode - Obtener modo Stripe](#30-get-apiadminstripemode)
31. [POST /api/Admin/stripe/mode - Establecer modo Stripe](#31-post-apiadminstripemode)
32. [POST /api/Admin/stripe/toggle-mode - Alternar modo Stripe](#32-post-apiadminstripetoggle-mode)

### **GESTIÓN DE SUSCRIPCIONES**
33. [POST /api/Subscription/force-finalize - Forzar finalización](#33-post-apisubscriptionforce-finalize)
34. [POST /api/Subscription/resolve-dispute - Resolver disputa](#34-post-apisubscriptionresolve-dispute)
35. [POST /api/Subscription/create-log-type-table - Crear tabla de logs](#35-post-apisubscriptioncreate-log-type-table)

### **GESTIÓN DE CHAT**
36. [GET /api/Chat/conversations - Todas las conversaciones](#36-get-apichatconversations)

### **GESTIÓN DE NOTIFICACIONES**
37. [POST /api/Notification - Crear notificación](#37-post-apinotification)
38. [PUT /api/Notification/{id}/read - Marcar como leída](#38-put-apinotificationidread)

### **GESTIÓN DE CITAS**
39. [GET /api/Appointment/admin/metrics - Métricas de citas](#39-get-apiappointmentadminmetrics)
40. [POST /api/Appointment/admin/check-timers - Verificar timers](#40-post-apiappointmentadmincheck-timers)

### **GESTIÓN DE ELIMINACIÓN DE CUENTAS**
41. [POST /api/AccountDeletion/admin/delete/{userId} - Eliminar cuenta](#41-post-apiaccountdeletionadmindeleteuserid)
42. [GET /api/AccountDeletion/admin/status/{userId} - Estado de eliminación](#42-get-apiaccountdeletionadminstatususerid)

---

## 🔐 AUTENTICACIÓN

**TODOS los endpoints requieren:**
```typescript
headers: {
  'Authorization': `Bearer ${accessToken}`,
  'Content-Type': 'application/json'
}
```

**El token debe tener:**
- Claim `Role` = `"Admin"` o `"2"`
- Claim `NameIdentifier` = ID del usuario admin

---

## 1️⃣ GET /api/User/all

**Descripción:** Obtiene lista paginada de todos los usuarios

**Query Parameters:**
```typescript
{
  page?: number;      // Default: 1, mínimo: 1
  pageSize?: number;  // Default: 20, rango: 1-50
}
```

**Request:**
```typescript
GET /api/User/all?page=1&pageSize=20
```

**Response (200 OK):**
```typescript
{
  users: Array<{
    id: number;
    name: string;
    email: string;
    phoneNumber: string | null;      // ⚠️ PUEDE SER NULL
    phoneVerified: boolean;
    isBlocked: boolean;
    createdAt: string;               // ISO 8601: "2024-01-20T10:30:00Z"
    searchCount: number;              // Número de búsquedas activas
    subscriptionPlan: string;         // "Free" | "Premium" | etc.
    role: string;                     // "Admin" | "User" | "Expert"
  }>;
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}
```

**Ejemplo:**
```json
{
  "users": [
    {
      "id": 1,
      "name": "Juan Pérez",
      "email": "juan@example.com",
      "phoneNumber": "+34612345678",
      "phoneVerified": true,
      "isBlocked": false,
      "createdAt": "2024-01-15T10:30:00Z",
      "searchCount": 5,
      "subscriptionPlan": "Premium",
      "role": "User"
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 150,
    "totalPages": 8,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

---

## 2️⃣ PUT /api/User/{userId}/block

**Descripción:** Bloquea o desbloquea un usuario

**Path Parameters:**
```typescript
userId: number;  // ID del usuario a bloquear/desbloquear
```

**Request:**
```typescript
PUT /api/User/123/block
// Body vacío (no requiere body)
```

**Response (200 OK):**
```typescript
// Body vacío (solo status 200)
```

**Errores:**
- `400 Bad Request`: No se puede bloquear este usuario (ej: es admin principal)
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

**Nota:** Este endpoint alterna el estado (si está bloqueado, lo desbloquea y viceversa)

---

## 3️⃣ DELETE /api/User/{userId}

**Descripción:** Elimina un usuario permanentemente

**Path Parameters:**
```typescript
userId: number;  // ID del usuario a eliminar
```

**Request:**
```typescript
DELETE /api/User/123
// Body vacío
```

**Response (200 OK):**
```typescript
// Body vacío (solo status 200)
```

**Errores:**
- `400 Bad Request`: No se puede eliminar este usuario (ej: es admin principal o tiene dependencias)
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

---

## 4️⃣ GET /api/Dispute/all

**Descripción:** Obtiene lista paginada de todas las disputas con filtros

**Query Parameters (DisputeListRequestDto):**
```typescript
{
  page?: number;              // Default: 1, mínimo: 1
  pageSize?: number;          // Default: 20, máximo: 50
  searchTerm?: string;        // Buscar en razón y comentarios
  status?: string;            // "Pending" | "Resolved" | "Closed"
  reporterId?: number;        // Filtrar por usuario que reportó
  clientId?: number;          // Filtrar por cliente
  expertId?: number;          // Filtrar por experto
  startDate?: string;         // ISO 8601 DateTime
  endDate?: string;           // ISO 8601 DateTime
  sortBy?: string;           // Default: "CreatedAt"
  sortDirection?: string;    // "asc" | "desc", Default: "desc"
}
```

**Request:**
```typescript
GET /api/Dispute/all?page=1&pageSize=20&status=Pending
```

**Response (200 OK) - DisputeListResponseDto:**
```typescript
{
  disputes: Array<DisputeDto>;
  pagination: PaginationMetadata;
  stats: DisputeStats;
}
```

**DisputeDto:**
```typescript
{
  id: number;
  searchHireId: number;
  reporterId: number;
  reason: string;
  status: string;                    // "Pending" | "Resolved" | "Closed"
  statusTranslated: string;          // ⭐ USAR ESTE: "Pendiente" | "Resuelta" | "Cerrada"
  resolutionComments: string | null; // ⚠️ PUEDE SER NULL
  createdAt: string;                 // ISO 8601
  expertResponse: string | null;     // ⚠️ PUEDE SER NULL
  expertResponseDeadline: string | null; // ⚠️ PUEDE SER NULL (ISO 8601)
  expertResponseAt: string | null;    // ⚠️ PUEDE SER NULL (ISO 8601)
  canExpertRespond: boolean;
  searchHire: {
    id: number;
    status: string;
    statusTranslated: string;        // ⭐ USAR ESTE
    amount: number;                  // Decimal
    createdAt: string;               // ISO 8601
  };
  reporter: {
    id: number;
    name: string;
    email: string;
  };
  client: {
    id: number;
    name: string;
    email: string;
  } | null;                          // ⚠️ PUEDE SER NULL
  expert: {
    id: number;
    name: string;
    email: string;
  } | null;                          // ⚠️ PUEDE SER NULL
  search: {
    id: number;
    title: string;
    description: string;             // ⚠️ Puede ser ""
    createdAt: string;               // ISO 8601
  };
  files: Array<{
    id: number;
    fileName: string;
    fileType: string;
    fileSize: number;
    createdAt: string;
    filePath: string;
    fileUrl: string;
    uploadedByUserId: number;
    uploadedByUserName: string;
    uploadedByUserEmail: string;
    fileCategory: string;            // "client" | "expert"
    fileCategoryLabel: string;       // ⭐ USAR ESTE: "Archivo del Cliente" | "Archivo del Experto"
  }>;                                // ⚠️ PUEDE ESTAR VACÍO []
}
```

**PaginationMetadata:**
```typescript
{
  currentPage: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPrevious: boolean;
  hasNext: boolean;
}
```

**DisputeStats:**
```typescript
{
  pendingDisputes: number;
  resolvedDisputes: number;
  clientDisputes: number;
  expertDisputes: number;
  thisWeekDisputes: number;
  thisMonthDisputes: number;
}
```

**Ejemplo:**
```json
{
  "disputes": [
    {
      "id": 1,
      "searchHireId": 123,
      "reporterId": 5,
      "reason": "El servicio no cumplió con lo prometido",
      "status": "Pending",
      "statusTranslated": "Pendiente",
      "resolutionComments": null,
      "createdAt": "2024-01-20T10:00:00Z",
      "expertResponse": null,
      "expertResponseDeadline": "2024-01-22T10:00:00Z",
      "expertResponseAt": null,
      "canExpertRespond": true,
      "searchHire": {
        "id": 123,
        "status": "Disputed",
        "statusTranslated": "En Disputa",
        "amount": 150.50,
        "createdAt": "2024-01-15T08:00:00Z"
      },
      "reporter": {
        "id": 5,
        "name": "Cliente Ejemplo",
        "email": "cliente@example.com"
      },
      "client": {
        "id": 5,
        "name": "Cliente Ejemplo",
        "email": "cliente@example.com"
      },
      "expert": {
        "id": 10,
        "name": "Experto Ejemplo",
        "email": "experto@example.com"
      },
      "search": {
        "id": 50,
        "title": "Inspección de iPhone 13",
        "description": "Necesito que revisen el estado del iPhone",
        "createdAt": "2024-01-10T12:00:00Z"
      },
      "files": [
        {
          "id": 1,
          "fileName": "foto-problema.jpg",
          "fileType": "jpg",
          "fileSize": 245678,
          "createdAt": "2024-01-20T10:05:00Z",
          "filePath": "https://storage.googleapis.com/...",
          "fileUrl": "https://storage.googleapis.com/...",
          "uploadedByUserId": 5,
          "uploadedByUserName": "Cliente Ejemplo",
          "uploadedByUserEmail": "cliente@example.com",
          "fileCategory": "client",
          "fileCategoryLabel": "Archivo del Cliente"
        }
      ]
    }
  ],
  "pagination": {
    "currentPage": 1,
    "pageSize": 20,
    "totalCount": 45,
    "totalPages": 3,
    "hasPrevious": false,
    "hasNext": true
  },
  "stats": {
    "pendingDisputes": 12,
    "resolvedDisputes": 30,
    "clientDisputes": 35,
    "expertDisputes": 7,
    "thisWeekDisputes": 5,
    "thisMonthDisputes": 18
  }
}
```

---

## 5️⃣ PUT /api/Dispute/{disputeId}/resolve

**Descripción:** Resuelve una disputa (solo admin)

**Path Parameters:**
```typescript
disputeId: number;  // ID de la disputa
```

**Request Body (ResolveDisputeDto):**
```typescript
{
  resolutionComments: string;  // REQUERIDO, máximo 1000 caracteres
  action: string;             // REQUERIDO: "refund_client" | "pay_expert"
}
```

**Request:**
```typescript
PUT /api/Dispute/1/resolve
Content-Type: application/json

{
  "resolutionComments": "Se reembolsó al cliente porque el servicio no cumplió con lo acordado",
  "action": "refund_client"
}
```

**Response (200 OK):**
```typescript
{
  message: string;
  disputeId: number;
  resolvedAt: string;  // ISO 8601
}
```

**Ejemplo:**
```json
{
  "message": "Dispute resolved successfully",
  "disputeId": 1,
  "resolvedAt": "2024-01-20T15:30:00Z"
}
```

**Errores:**
- `400 Bad Request`: 
  - "Los comentarios de resolución son obligatorios"
  - "La acción es obligatoria"
  - "La acción debe ser 'refund_client' o 'pay_expert'"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: Disputa no encontrada
- `500 Internal Server Error`: Error del servidor

---

## 6️⃣ GET /api/Dispute/{disputeId}

**Descripción:** Obtiene detalles de una disputa específica

**Path Parameters:**
```typescript
disputeId: number;  // ID de la disputa
```

**Request:**
```typescript
GET /api/Dispute/1
```

**Response (200 OK):**
```typescript
// Misma estructura que DisputeDto en el endpoint /all
DisputeDto
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: Disputa no encontrada

---

## 7️⃣ GET /api/Log/critical

**Descripción:** Obtiene logs críticos paginados

**Query Parameters:**
```typescript
{
  page?: number;      // Default: 1
  pageSize?: number;  // Default: 20, máximo: 50
}
```

**Request:**
```typescript
GET /api/Log/critical?page=1&pageSize=20
```

**Response (200 OK):**
```typescript
{
  logs: Array<{
    id: number;
    message: string;
    details: string;
    createdAt: string;              // ISO 8601
    logType: {
      id: number;
      name: string;
      description: string | null;
      severityId: number;
    } | null;                       // ⚠️ PUEDE SER NULL
    user: {
      id: number;
      name: string;
      email: string;
    } | null;                       // ⚠️ PUEDE SER NULL
    additionalData: object | null;  // ⚠️ PUEDE SER NULL (JSON object)
  }>;
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}
```

**Ejemplo:**
```json
{
  "logs": [
    {
      "id": 1,
      "message": "CRITICAL: Failed to process payment",
      "details": "Stripe payment failed for user 123",
      "createdAt": "2024-01-20T15:30:00Z",
      "logType": {
        "id": 1,
        "name": "Critical",
        "description": "Critical system errors",
        "severityId": 1
      },
      "user": {
        "id": 123,
        "name": "Usuario Ejemplo",
        "email": "usuario@example.com"
      },
      "additionalData": {
        "paymentId": "pi_123456",
        "amount": 150.50,
        "error": "Card declined"
      }
    }
  ],
  "pagination": {
    "page": 1,
    "pageSize": 20,
    "totalCount": 150,
    "totalPages": 8,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

---

## 8️⃣ GET /api/Log/types

**Descripción:** Obtiene todos los tipos de logs activos

**Request:**
```typescript
GET /api/Log/types
```

**Response (200 OK):**
```typescript
Array<{
  id: number;
  name: string;
  description: string | null;
  category: string;
  severity: string;
  requiresAdminNotification: boolean;
  requiresEmailAlert: boolean;
  requiresSmsAlert: boolean;
  isActive: boolean;
  createdAt: string;
  updatedAt: string | null;
}>
```

**Nota:** Este endpoint NO requiere ser admin, pero está incluido aquí porque es útil para el admin panel.

---

## 9️⃣ GET /api/SystemStatus/statuses

**Descripción:** Obtiene todos los estados del sistema

**Query Parameters:**
```typescript
{
  statusType?: string;  // Filtrar por tipo (ej: "SearchHireStatus")
}
```

**Request:**
```typescript
GET /api/SystemStatus/statuses?statusType=SearchHireStatus
```

**Response (200 OK):**
```typescript
Array<{
  id: number;
  statusType: string;        // "SearchHireStatus" | "DisputeStatus" | etc.
  statusName: string;        // Nombre técnico
  statusValue: string;       // Valor técnico (ej: "Pending")
  displayName: string;       // ⭐ USAR ESTE: Nombre para mostrar
  description: string | null;
  sortOrder: number;
  createdAt: string;         // ISO 8601
  updatedAt: string;         // ISO 8601
}>
```

**Ejemplo:**
```json
[
  {
    "id": 1,
    "statusType": "SearchHireStatus",
    "statusName": "Pending",
    "statusValue": "Pending",
    "displayName": "Pendiente",
    "description": "Esperando confirmación",
    "sortOrder": 1,
    "createdAt": "2024-01-01T00:00:00Z",
    "updatedAt": "2024-01-01T00:00:00Z"
  }
]
```

---

## 🔟 POST /api/SystemStatus/statuses

**Descripción:** Crea un nuevo estado del sistema (solo admin)

**Request Body (CreateSystemStatusRequest):**
```typescript
{
  statusType: string;      // REQUERIDO: "SearchHireStatus" | "DisputeStatus" | etc.
  statusName: string;      // REQUERIDO: Nombre técnico
  statusValue: string;      // REQUERIDO: Valor técnico (único por statusType)
  displayName: string;     // REQUERIDO: Nombre para mostrar
  description?: string;    // Opcional
  sortOrder: number;       // Default: 0
}
```

**Request:**
```typescript
POST /api/SystemStatus/statuses
Content-Type: application/json

{
  "statusType": "SearchHireStatus",
  "statusName": "InReview",
  "statusValue": "in_review",
  "displayName": "En Revisión",
  "description": "Servicio en proceso de revisión",
  "sortOrder": 3
}
```

**Response (201 Created):**
```typescript
{
  id: number;
  statusType: string;
  statusName: string;
  statusValue: string;
  displayName: string;
  description: string | null;
  sortOrder: number;
  createdAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: 
  - "Ya existe un estado con el valor 'X' en el tipo 'Y'"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

---

## 1️⃣1️⃣ PUT /api/SystemStatus/statuses/{statusId}

**Descripción:** Actualiza un estado del sistema (solo admin)

**Path Parameters:**
```typescript
statusId: number;  // ID del estado
```

**Request Body (UpdateSystemStatusRequest):**
```typescript
{
  statusType: string;      // REQUERIDO
  statusName: string;      // REQUERIDO
  statusValue: string;     // REQUERIDO
  displayName: string;     // REQUERIDO
  description?: string;
  sortOrder: number;
  isActive: boolean;        // REQUERIDO
}
```

**Request:**
```typescript
PUT /api/SystemStatus/statuses/1
Content-Type: application/json

{
  "statusType": "SearchHireStatus",
  "statusName": "Pending",
  "statusValue": "Pending",
  "displayName": "Pendiente",
  "description": "Esperando confirmación",
  "sortOrder": 1,
  "isActive": true
}
```

**Response (200 OK):**
```typescript
{
  id: number;
  statusType: string;
  statusName: string;
  statusValue: string;
  displayName: string;
  description: string | null;
  sortOrder: number;
  isActive: boolean;
  updatedAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: 
  - "Ya existe un estado con el valor 'X' en el tipo 'Y'"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: Estado no encontrado
- `500 Internal Server Error`: Error del servidor

---

## 1️⃣2️⃣ DELETE /api/SystemStatus/statuses/{statusId}

**Descripción:** Elimina un estado del sistema (solo admin)

**Path Parameters:**
```typescript
statusId: number;  // ID del estado
```

**Request:**
```typescript
DELETE /api/SystemStatus/statuses/1
```

**Response (200 OK):**
```typescript
{
  message: "Estado eliminado exitosamente"
}
```

**Errores:**
- `400 Bad Request`: 
  - "No se puede eliminar el estado porque tiene mapeos asociados"
  - "No se puede eliminar el estado porque tiene configuraciones asociadas"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: Estado no encontrado

---

## 1️⃣3️⃣ GET /api/SystemStatus/mappings

**Descripción:** Obtiene todos los mapeos de estados

**Query Parameters:**
```typescript
{
  page?: number;      // Default: 1
  pageSize?: number;  // Default: 20, máximo: 50
}
```

**Request:**
```typescript
GET /api/SystemStatus/mappings?page=1&pageSize=20
```

**Response (200 OK):**
```typescript
{
  mappings: Array<{
    id: number;
    sourceStatus: {
      id: number;
      statusType: string;
      statusName: string;
      statusValue: string;
      displayName: string;
    };
    targetStatus: {
      id: number;
      statusType: string;
      statusName: string;
      statusValue: string;
      displayName: string;
    };
    isActive: boolean;
    createdAt: string;  // ISO 8601
  }>;
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}
```

---

## 1️⃣4️⃣ POST /api/SystemStatus/mappings

**Descripción:** Crea un nuevo mapeo de estados (solo admin)

**Request Body (CreateStatusMappingRequest):**
```typescript
{
  sourceStatusId: number;  // REQUERIDO: ID del estado origen
  targetStatusId: number;  // REQUERIDO: ID del estado destino
}
```

**Request:**
```typescript
POST /api/SystemStatus/mappings
Content-Type: application/json

{
  "sourceStatusId": 1,
  "targetStatusId": 2
}
```

**Response (201 Created):**
```typescript
{
  id: number;
  sourceStatus: {
    id: number;
    statusValue: string;
    displayName: string;
  };
  targetStatus: {
    id: number;
    statusValue: string;
    displayName: string;
  };
  isActive: boolean;
  createdAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: 
  - "Uno o ambos estados no existen"
  - "Ya existe este mapeo de estados"
- `401 Unauthorized`: Token inválido o sin rol Admin

---

## 1️⃣5️⃣ PUT /api/SystemStatus/mappings/{mappingId}

**Descripción:** Actualiza un mapeo de estados (solo admin)

**Path Parameters:**
```typescript
mappingId: number;  // ID del mapeo
```

**Request Body (UpdateStatusMappingRequest):**
```typescript
{
  sourceStatusId: number;  // REQUERIDO
  targetStatusId: number;  // REQUERIDO
  isActive: boolean;       // REQUERIDO
}
```

**Request:**
```typescript
PUT /api/SystemStatus/mappings/1
Content-Type: application/json

{
  "sourceStatusId": 1,
  "targetStatusId": 2,
  "isActive": true
}
```

**Response (200 OK):**
```typescript
{
  id: number;
  sourceStatus: {
    id: number;
    statusValue: string;
    displayName: string;
  };
  targetStatus: {
    id: number;
    statusValue: string;
    displayName: string;
  };
  isActive: boolean;
}
```

**Errores:**
- `400 Bad Request`: 
  - "Uno o ambos estados no existen"
  - "Ya existe este mapeo de estados"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: Mapeo no encontrado

---

## 1️⃣6️⃣ DELETE /api/SystemStatus/mappings/{mappingId}

**Descripción:** Elimina un mapeo de estados (solo admin)

**Path Parameters:**
```typescript
mappingId: number;  // ID del mapeo
```

**Request:**
```typescript
DELETE /api/SystemStatus/mappings/1
```

**Response (200 OK):**
```typescript
{
  message: "Mapeo eliminado exitosamente"
}
```

---

## 1️⃣7️⃣ GET /api/SystemStatus/configurations

**Descripción:** Obtiene configuraciones de distribución de dinero

**Query Parameters:**
```typescript
{
  statusValue?: string;  // Filtrar por valor de estado
  page?: number;         // Default: 1
  pageSize?: number;     // Default: 20, máximo: 50
}
```

**Request:**
```typescript
GET /api/SystemStatus/configurations?statusValue=completed&page=1&pageSize=20
```

**Response (200 OK):**
```typescript
{
  configurations: Array<{
    id: number;
    status: {
      id: number;
      statusType: string;
      statusName: string;
      statusValue: string;
      displayName: string;
    };
    category: {
      id: number;
      name: string;
    } | null;                    // ⚠️ PUEDE SER NULL
    serviceTypeCategory: {
      id: number;
      name: string;
    } | null;                    // ⚠️ PUEDE SER NULL
    clientPercentage: number;    // Decimal (ej: 0.0, 90.0)
    expertPercentage: number;    // Decimal (ej: 95.0, 8.0)
    platformPercentage: number; // Decimal (ej: 5.0, 2.0)
    isActive: boolean;
    createdAt: string;           // ISO 8601
    updatedAt: string;           // ISO 8601
  }>;
  pagination: {
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
    hasNextPage: boolean;
    hasPreviousPage: boolean;
  };
}
```

**Nota:** Los porcentajes deben sumar 100.0

---

## 1️⃣8️⃣ POST /api/SystemStatus/configurations

**Descripción:** Crea una nueva configuración de distribución de dinero (solo admin)

**Request Body (CreateStatusConfigurationRequest):**
```typescript
{
  statusId: number;              // REQUERIDO: ID del estado
  categoryId?: number | null;     // Opcional: ID de categoría (null = por defecto)
  serviceTypeCategoryId?: number | null;  // Opcional: ID de tipo de servicio (null = por defecto)
  clientPercentage: number;       // REQUERIDO: Decimal (0-100)
  expertPercentage: number;       // REQUERIDO: Decimal (0-100)
  platformPercentage: number;   // REQUERIDO: Decimal (0-100)
}
```

**Request:**
```typescript
POST /api/SystemStatus/configurations
Content-Type: application/json

{
  "statusId": 5,
  "categoryId": null,
  "serviceTypeCategoryId": null,
  "clientPercentage": 0.0,
  "expertPercentage": 95.0,
  "platformPercentage": 5.0
}
```

**Response (201 Created):**
```typescript
{
  id: number;
  status: {
    id: number;
    statusValue: string;
    displayName: string;
  };
  categoryId: number | null;
  serviceTypeCategoryId: number | null;
  clientPercentage: number;
  expertPercentage: number;
  platformPercentage: number;
  createdAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: 
  - "El estado especificado no existe"
  - "Los porcentajes deben sumar exactamente 100%"
  - "Ya existe una configuración para esta combinación de estado, categoría y tipo de servicio"
- `401 Unauthorized`: Token inválido o sin rol Admin

---

## 1️⃣9️⃣ POST /api/Categories

**Descripción:** Crea una nueva categoría o subcategoría (solo admin)

**Request Body (CreateCategoryDto):**
```typescript
{
  name: string;        // REQUERIDO: Nombre de la categoría
  parentId?: number | null;  // Opcional: ID de categoría padre (null = categoría principal)
  isActive?: boolean;  // Default: true
}
```

**Request:**
```typescript
POST /api/Categories
Content-Type: application/json

{
  "name": "Electrónica",
  "parentId": null,
  "isActive": true
}
```

**Response (201 Created):**
```typescript
{
  success: boolean;
  message: string;  // Ej: "categoría 'Electrónica' creada exitosamente"
  data: {
    id: number;
    name: string;
    parentId: number | null;
    isActive: boolean;
    createdAt: string;  // ISO 8601
    updatedAt: string;  // ISO 8601
  };
}
```

**Errores:**
- `400 Bad Request`: 
  - "El nombre de la categoría es requerido"
  - "Ya existe una categoría con el nombre 'X'"
  - "La categoría padre con ID X no existe"
  - "La categoría seleccionada es una subcategoría. Solo se pueden seleccionar categorías padre"
  - "La categoría padre seleccionada no está activa"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error de base de datos

---

## 2️⃣0️⃣ POST /api/Categories/fix-sequence

**Descripción:** Corrige la secuencia de IDs de categorías (solo admin)

**Request:**
```typescript
POST /api/Categories/fix-sequence
// Body vacío
```

**Response (200 OK):**
```typescript
{
  message: "Secuencia de IDs corregida exitosamente",
  maxId: number,
  nextId: number
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error de base de datos

---

## 2️⃣1️⃣ POST /api/ServiceType

**Descripción:** Crea un nuevo tipo de servicio (solo admin)

**Request Body (ServiceTypeDto - solo campos requeridos para crear):**
```typescript
{
  name: string;                    // REQUERIDO
  description?: string;            // Opcional, default: ""
  position?: number;               // Opcional, default: 0
  isActive?: boolean;              // Opcional, default: true
  serviceTypeCategoryId?: number | null;  // Opcional
  requiresAppointment?: boolean;   // Opcional, default: false
}
```

**Request:**
```typescript
POST /api/ServiceType
Content-Type: application/json

{
  "name": "Inspección de Vehículos",
  "description": "Revisión completa del estado de vehículos",
  "position": 1,
  "isActive": true,
  "serviceTypeCategoryId": 1,
  "requiresAppointment": true
}
```

**Response (201 Created):**
```typescript
{
  id: number;
  name: string;
  description: string;
  position: number;
  isActive: boolean;
  serviceTypeCategoryId: number | null;
  serviceTypeCategoryName: string | null;
  requiresAppointment: boolean;
  createdAt: string;  // ISO 8601
  updatedAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: "Name is required"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `500 Internal Server Error`: Error del servidor

---

## 2️⃣2️⃣ PUT /api/ServiceType/{id}

**Descripción:** Actualiza un tipo de servicio (solo admin)

**Path Parameters:**
```typescript
id: number;  // ID del tipo de servicio
```

**Request Body (ServiceTypeDto completo):**
```typescript
{
  id: number;                     // REQUERIDO: Debe coincidir con el ID de la URL
  name: string;                    // REQUERIDO
  description?: string;
  position?: number;
  isActive?: boolean;
  serviceTypeCategoryId?: number | null;
  requiresAppointment?: boolean;
}
```

**Request:**
```typescript
PUT /api/ServiceType/1
Content-Type: application/json

{
  "id": 1,
  "name": "Inspección de Vehículos Actualizada",
  "description": "Nueva descripción",
  "position": 1,
  "isActive": true,
  "serviceTypeCategoryId": 1,
  "requiresAppointment": true
}
```

**Response (200 OK):**
```typescript
// Misma estructura que el POST, pero con updatedAt actualizado
{
  id: number;
  name: string;
  description: string;
  position: number;
  isActive: boolean;
  serviceTypeCategoryId: number | null;
  serviceTypeCategoryName: string | null;
  requiresAppointment: boolean;
  createdAt: string;  // ISO 8601
  updatedAt: string;  // ISO 8601 (actualizado)
}
```

**Errores:**
- `400 Bad Request`: 
  - "ID mismatch"
  - "Name is required"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `404 Not Found`: "Service type not found"
- `500 Internal Server Error`: Error del servidor

---

## 2️⃣3️⃣ DELETE /api/ServiceType/{id}

**Descripción:** Elimina un tipo de servicio (solo admin)

**Path Parameters:**
```typescript
id: number;  // ID del tipo de servicio
```

**Request:**
```typescript
DELETE /api/ServiceType/1
```

**Response (200 OK):**
```typescript
{
  message: "Service type deleted successfully"
}
```

**Errores:**
- `400 Bad Request`: "Cannot delete service type with associated search parameters or services"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `404 Not Found`: "Service type not found"

---

## 2️⃣4️⃣ POST /api/ServiceTypeCategory

**Descripción:** Crea una nueva categoría de tipo de servicio (solo admin)

**Request Body (CreateServiceTypeCategoryDto):**
```typescript
{
  name: string;        // REQUERIDO
  description?: string;  // Opcional
}
```

**Request:**
```typescript
POST /api/ServiceTypeCategory
Content-Type: application/json

{
  "name": "Inspecciones",
  "description": "Categoría para servicios de inspección"
}
```

**Response (201 Created):**
```typescript
{
  id: number;
  name: string;
  description: string | null;
  createdAt: string;  // ISO 8601
  updatedAt: string;  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: "Name is required"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"

---

## 2️⃣5️⃣ PUT /api/ServiceTypeCategory/{id}

**Descripción:** Actualiza una categoría de tipo de servicio (solo admin)

**Path Parameters:**
```typescript
id: number;  // ID de la categoría
```

**Request Body (UpdateServiceTypeCategoryDto):**
```typescript
{
  name: string;        // REQUERIDO
  description?: string;
}
```

**Request:**
```typescript
PUT /api/ServiceTypeCategory/1
Content-Type: application/json

{
  "name": "Inspecciones Actualizadas",
  "description": "Nueva descripción"
}
```

**Response (200 OK):**
```typescript
{
  id: number;
  name: string;
  description: string | null;
  createdAt: string;  // ISO 8601
  updatedAt: string;  // ISO 8601 (actualizado)
}
```

---

## 2️⃣6️⃣ DELETE /api/ServiceTypeCategory/{id}

**Descripción:** Elimina una categoría de tipo de servicio (solo admin)

**Path Parameters:**
```typescript
id: number;  // ID de la categoría
```

**Request:**
```typescript
DELETE /api/ServiceTypeCategory/1
```

**Response (200 OK):**
```typescript
{
  message: "Service type category deleted successfully"
}
```

**Errores:**
- `400 Bad Request`: "Cannot delete category with associated service types"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `404 Not Found`: "Service type category not found"

---

## 2️⃣7️⃣ GET /api/AppointmentConfig/appointment-status-configs

**Descripción:** Obtiene configuraciones de distribución de dinero por estado de finalización

**Request:**
```typescript
GET /api/AppointmentConfig/appointment-status-configs
```

**Response (200 OK):**
```typescript
Array<{
  id: number;
  estado: string;              // ⭐ DisplayName del estado
  statusId: number;
  statusValue: string;
  statusName: string;
  cliente: number;             // Porcentaje para cliente
  experto: number;             // Porcentaje para experto
  plataforma: number;          // Porcentaje para plataforma
  prioridad: string;           // "Nivel 4 - Por Defecto"
  activo: string;              // "Activo" | "Inactivo"
  categoryId: number | null;
  categoryName: string;        // "Todas las categorías" si categoryId es null
  serviceTypeCategoryId: number | null;
  serviceTypeCategoryName: string;  // "Todos los tipos" si serviceTypeCategoryId es null
  createdAt: string;           // ISO 8601
  updatedAt: string;           // ISO 8601
}>
```

**Ejemplo:**
```json
[
  {
    "id": 29,
    "estado": "Completado",
    "statusId": 5,
    "statusValue": "completed",
    "statusName": "Completed",
    "cliente": 0.0,
    "experto": 95.0,
    "plataforma": 5.0,
    "prioridad": "Nivel 4 - Por Defecto",
    "activo": "Activo",
    "categoryId": null,
    "categoryName": "Todas las categorías",
    "serviceTypeCategoryId": null,
    "serviceTypeCategoryName": "Todos los tipos",
    "createdAt": "2024-01-20T10:00:00Z",
    "updatedAt": "2024-01-20T10:00:00Z"
  }
]
```

**Nota:** Este endpoint devuelve solo configuraciones por defecto (sin categoría ni tipo de servicio específico) para estados de finalización.

---

## 2️⃣8️⃣ GET /api/Admin/suspicious-users

**Descripción:** Detecta usuarios con actividad sospechosa

**Query Parameters:**
```typescript
{
  minutes?: number;              // Default: 15 (ventana de tiempo)
  minRequestsPerMinute?: number; // Default: 50
  maxFailedAuthAttempts?: number; // Default: 5
}
```

**Request:**
```typescript
GET /api/Admin/suspicious-users?minutes=15&minRequestsPerMinute=50
```

**Response (200 OK):**
```typescript
{
  success: boolean;
  timestamp: string;             // ISO 8601
  windowMinutes: number;
  suspiciousUsersCount: number;
  suspiciousUsers: Array<{
    userId: number;
    email: string;
    name: string;
    lastActivity: string;        // ISO 8601
    suspiciousReasons: string[]; // Array de razones
    riskScore: number;           // 0-100
  }>;
  criteria: {
    minRequestsPerMinute: number;
    maxFailedAuthAttempts: number;
    offHoursDetection: boolean;
  };
}
```

---

## 2️⃣9️⃣ POST /api/Admin/block-user/{userId}

**Descripción:** Bloquea un usuario sospechoso

**Path Parameters:**
```typescript
userId: number;  // ID del usuario
```

**Request Body:**
```typescript
// Body es un string (razón del bloqueo)
"Usuario con actividad sospechosa detectada"
```

**Request:**
```typescript
POST /api/Admin/block-user/123
Content-Type: application/json

"Usuario con actividad sospechosa detectada"
```

**Response (200 OK):**
```typescript
{
  success: boolean;
  message: string;  // Ej: "User usuario@example.com has been blocked"
  userId: number;
  blockedAt: string;  // ISO 8601
  reason: string;
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: "User not found"
- `500 Internal Server Error`: Error del servidor

---

## 3️⃣0️⃣ GET /api/Admin/stripe/mode

**Descripción:** Obtiene el modo actual de Stripe

**Request:**
```typescript
GET /api/Admin/stripe/mode
```

**Response (200 OK):**
```typescript
{
  mode: string;                  // "development" | "production"
  changedAt: string | null;      // ⚠️ PUEDE SER NULL (ISO 8601)
  changedByUserId: number | null; // ⚠️ PUEDE SER NULL
}
```

**Ejemplo:**
```json
{
  "mode": "development",
  "changedAt": "2024-01-15T10:00:00Z",
  "changedByUserId": 1
}
```

---

## 3️⃣1️⃣ POST /api/Admin/stripe/mode

**Descripción:** Establece el modo de Stripe

**Request Body:**
```typescript
{
  mode: string;  // REQUERIDO: "development" | "production"
}
```

**Request:**
```typescript
POST /api/Admin/stripe/mode
Content-Type: application/json

{
  "mode": "development"
}
```

**Response (200 OK):**
```typescript
{
  success: boolean;
  message: string;  // Ej: "Modo Stripe cambiado a development"
  mode: string;
  warning: string;  // Advertencia sobre webhooks
}
```

**Errores:**
- `400 Bad Request`: 
  - "El campo 'Mode' es requerido"
  - "El modo debe ser 'development' o 'production'"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error cambiando modo Stripe

---

## 3️⃣2️⃣ POST /api/Admin/stripe/toggle-mode

**Descripción:** Alterna el modo de Stripe automáticamente (development ↔ production)

**Request:**
```typescript
POST /api/Admin/stripe/toggle-mode
// Body vacío
```

**Response (200 OK):**
```typescript
{
  success: boolean;
  message: string;  // Ej: "Modo Stripe cambiado de development a production"
  previousMode: string;
  newMode: string;
  warning: string;
}
```

---

## 3️⃣3️⃣ POST /api/Subscription/force-finalize

**Descripción:** Fuerza la finalización de un servicio (solo admin)

**Request Body (ForceFinalizeDto):**
```typescript
{
  searchHireId: number;          // REQUERIDO
  resolveInFavorOfClient: boolean; // REQUERIDO: true = reembolsar cliente, false = no soportado
}
```

**Request:**
```typescript
POST /api/Subscription/force-finalize
Content-Type: application/json

{
  "searchHireId": 123,
  "resolveInFavorOfClient": true
}
```

**Response (200 OK):**
```typescript
{
  message: "Service finalized successfully in favor of client"
}
```

**Errores:**
- `400 Bad Request`: 
  - "Force finalize in favor of expert is no longer supported. Use dispute resolution instead."
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: "Service not found"
- `500 Internal Server Error`: "Failed to process client refund" | "Failed to finalize service"

---

## 3️⃣4️⃣ POST /api/Subscription/resolve-dispute

**Descripción:** Resuelve una disputa desde el panel de suscripciones (solo admin)

**Request Body (ResolveDisputeDto):**
```typescript
{
  searchHireId: number;  // REQUERIDO
  resolution: string;    // REQUERIDO: Razón de la resolución
}
```

**Request:**
```typescript
POST /api/Subscription/resolve-dispute
Content-Type: application/json

{
  "searchHireId": 123,
  "resolution": "Se reembolsó al cliente porque el servicio no cumplió con lo acordado"
}
```

**Response (200 OK):**
```typescript
{
  message: "Dispute resolved successfully",
  searchHireId: number,
  resolvedAt: string  // ISO 8601
}
```

**Errores:**
- `400 Bad Request`: 
  - "Resolution reason is required"
  - "Service is not disputed"
- `401 Unauthorized`: Token inválido o sin rol Admin
- `404 Not Found`: 
  - "Service not found"
  - "No pending dispute found"
- `500 Internal Server Error`: Error del servidor

---

## 3️⃣5️⃣ POST /api/Subscription/create-log-type-table

**Descripción:** Crea la tabla de tipos de logs y datos iniciales (solo admin)

**Request:**
```typescript
POST /api/Subscription/create-log-type-table
// Body vacío
```

**Response (200 OK):**
```typescript
{
  message: "LogType table and data created successfully!",
  details: {
    tableCreated: "LogTypes",
    columnsAdded: string[],
    indexCreated: string,
    foreignKeyCreated: string,
    logTypesInserted: number
  }
}
```

**Nota:** Este endpoint es principalmente para setup inicial. No debería llamarse frecuentemente.

---

## 3️⃣6️⃣ GET /api/Chat/conversations

**Descripción:** Obtiene todas las conversaciones (solo admin)

**Request:**
```typescript
GET /api/Chat/conversations
```

**Response (200 OK):**
```typescript
Array<ConversationDto>
```

**ConversationDto:**
```typescript
{
  id: number;
  clientId: number | null;
  expertId: number | null;
  searchHireId: number | null;
  client: {
    id: number;
    name: string;
    email: string;
  } | null;
  expert: {
    id: number;
    name: string;
    email: string;
  } | null;
  searchHire: {
    id: number;
    search: {
      id: number;
      title: string;
    } | null;
  } | null;
  messages: Array<{
    id: number;
    content: string;
    senderId: number;
    sender: {
      id: number;
      name: string;
      email: string;
    };
    attachments: Array<{
      id: number;
      fileName: string;
      fileUrl: string;  // Signed URL
    }>;
    createdAt: string;  // ISO 8601
  }>;
  createdAt: string;    // ISO 8601
  updatedAt: string;    // ISO 8601
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `500 Internal Server Error`: Error del servidor

---

## 3️⃣7️⃣ POST /api/Notification

**Descripción:** Crea una notificación (solo admin)

**Request Body (CreateNotificationDto):**
```typescript
{
  title: string;        // REQUERIDO
  message: string;      // REQUERIDO
  type: string;         // REQUERIDO: Tipo de notificación
  userId?: number | null; // Opcional: null = broadcast (todos los usuarios)
}
```

**Request:**
```typescript
POST /api/Notification
Content-Type: application/json

{
  "title": "Mantenimiento programado",
  "message": "El sistema estará en mantenimiento el día X",
  "type": "info",
  "userId": null  // null = notificación para todos
}
```

**Response (200 OK):**
```typescript
{
  id: string;           // GUID
  title: string;
  message: string;
  type: string;
  userId: number | null;
  read: boolean;
  readAt: string | null;
  createdAt: string;    // ISO 8601
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

---

## 3️⃣8️⃣ PUT /api/Notification/{id}/read

**Descripción:** Marca una notificación como leída

**Path Parameters:**
```typescript
id: string;  // GUID de la notificación
```

**Request:**
```typescript
PUT /api/Notification/123e4567-e89b-12d3-a456-426614174000/read
```

**Response (200 OK):**
```typescript
{
  message: "Notification marked as read"
}
```

**Nota:** Los admins pueden marcar como leídas las notificaciones broadcast (userId = null)

---

## 3️⃣9️⃣ GET /api/Appointment/admin/metrics

**Descripción:** Obtiene métricas de citas (solo admin)

**Request:**
```typescript
GET /api/Appointment/admin/metrics
```

**Response (200 OK):**
```typescript
AppointmentMetricsDto
```

**AppointmentMetricsDto:**
```typescript
{
  totalAppointments: number;
  pendingAppointments: number;
  confirmedAppointments: number;
  completedAppointments: number;
  cancelledAppointments: number;
  averageResponseTime: number;  // En minutos
  // ... otros campos de métricas
}
```

**Errores:**
- `401 Unauthorized`: Token inválido o sin rol Admin
- `403 Forbid`: "Admin access required"
- `500 Internal Server Error`: Error del servidor

---

## 4️⃣0️⃣ POST /api/Appointment/admin/check-timers

**Descripción:** Verifica y procesa timers de citas (solo admin)

**Request:**
```typescript
POST /api/Appointment/admin/check-timers
// Body vacío
```

**Response (200 OK):**
```typescript
{
  message: "Appointment timers checked successfully"
}
```

**Nota:** Este endpoint ejecuta la lógica de verificación de timers de citas. Útil para debugging o ejecución manual.

---

## 4️⃣1️⃣ POST /api/AccountDeletion/admin/delete/{userId}

**Descripción:** Elimina la cuenta de cualquier usuario (solo admin)

**Path Parameters:**
```typescript
userId: number;  // ID del usuario a eliminar
```

**Request Body (AccountDeletionRequestDto):**
```typescript
{
  reason: string;  // REQUERIDO: Razón de la eliminación
  feedback?: string;  // Opcional: Feedback del usuario
}
```

**Request:**
```typescript
POST /api/AccountDeletion/admin/delete/123
Content-Type: application/json

{
  "reason": "Violación de términos de servicio",
  "feedback": null
}
```

**Response (200 OK):**
```typescript
AccountDeletionResponseDto
```

**AccountDeletionResponseDto:**
```typescript
{
  success: boolean;
  message: string;
  deletionScheduledAt: string | null;  // ISO 8601 o null si se elimina inmediatamente
  userId: number;
}
```

**Errores:**
- `400 Bad Request`: 
  - "Request body is required"
  - Razones de validación del servicio
- `401 Unauthorized`: Token inválido o sin rol Admin
- `500 Internal Server Error`: Error del servidor

---

## 4️⃣2️⃣ GET /api/AccountDeletion/admin/status/{userId}

**Descripción:** Obtiene el estado de eliminación de cualquier usuario (solo admin)

**Path Parameters:**
```typescript
userId: number;  // ID del usuario
```

**Request:**
```typescript
GET /api/AccountDeletion/admin/status/123
```

**Response (200 OK):**
```typescript
AccountDeletionStatusDto
```

**AccountDeletionStatusDto:**
```typescript
{
  isScheduledForDeletion: boolean;
  scheduledDeletionDate: string | null;  // ISO 8601 o null
  canCancel: boolean;
  daysUntilDeletion: number | null;
}
```

---

## 🚨 ERRORES COMUNES Y SOLUCIONES

### **Error 1: "Cannot read property 'name' of null"**
```typescript
// ❌ PROBLEMA
const expertName = dispute.expert.name;

// ✅ SOLUCIÓN
const expertName = dispute.expert?.name ?? "Experto no disponible";
```

### **Error 2: "statusTranslated is undefined"**
```typescript
// ❌ PROBLEMA
<div>{dispute.statusTranslated}</div>

// ✅ SOLUCIÓN
<div>{dispute.statusTranslated ?? dispute.status}</div>
```

### **Error 3: "files[0] is undefined"**
```typescript
// ❌ PROBLEMA
<img src={dispute.files[0].fileUrl} />

// ✅ SOLUCIÓN
{dispute.files && dispute.files.length > 0 ? (
  <img src={dispute.files[0].fileUrl} />
) : (
  <span>Sin archivos</span>
)}
```

### **Error 4: "pagination.hasNextPage is undefined"**
```typescript
// ❌ PROBLEMA
if (response.pagination.hasNextPage) { ... }

// ✅ SOLUCIÓN
if (response.pagination?.hasNextPage) { ... }
```

### **Error 5: Campos null en objetos anidados**
```typescript
// ❌ PROBLEMA
const categoryName = config.category.name;

// ✅ SOLUCIÓN
const categoryName = config.category?.name ?? "Todas las categorías";
```

---

## 📝 CHECKLIST DE IMPLEMENTACIÓN

### ✅ Verificaciones Obligatorias:

1. **✅ Siempre verificar campos opcionales (null):**
   ```typescript
   const value = data.field ?? "Valor por defecto";
   ```

2. **✅ Usar campos traducidos cuando existan:**
   ```typescript
   <div>{dispute.statusTranslated}</div>  // En lugar de dispute.status
   <div>{status.displayName}</div>        // En lugar de status.statusValue
   ```

3. **✅ Verificar arrays antes de acceder:**
   ```typescript
   {files.length > 0 && <img src={files[0].fileUrl} />}
   ```

4. **✅ Manejar paginación correctamente:**
   ```typescript
   const { data, pagination } = response;
   if (pagination?.hasNextPage) {
     // Mostrar botón "Cargar más"
   }
   ```

5. **✅ Validar tipos de datos:**
   ```typescript
   const amount = typeof dispute.searchHire.amount === 'number' 
     ? dispute.searchHire.amount.toFixed(2) 
     : '0.00';
   ```

6. **✅ Formatear fechas correctamente:**
   ```typescript
   const date = new Date(dispute.createdAt);
   const formatted = date.toLocaleDateString('es-ES');
   ```

7. **✅ Manejar errores HTTP:**
   ```typescript
   try {
     const response = await api.get('/api/User/all');
     // Procesar respuesta
   } catch (error) {
     if (error.response?.status === 401) {
       // Redirigir a login
     } else if (error.response?.status === 403) {
       // Mostrar error de permisos
     }
   }
   ```

---

## 🎯 RESUMEN FINAL

1. **✅ TODOS los endpoints requieren token JWT con rol Admin**
2. **✅ SIEMPRE verificar campos opcionales (null)**
3. **✅ USAR campos traducidos** (`statusTranslated`, `displayName`, `fileCategoryLabel`)
4. **✅ VERIFICAR arrays antes de acceder** (`files.length > 0`)
5. **✅ MANEJAR paginación correctamente**
6. **✅ VALIDAR tipos de datos** antes de usar
7. **✅ FORMATEAR fechas** correctamente
8. **✅ MANEJAR errores HTTP** apropiadamente

**Con esta guía, el frontend puede implementar correctamente TODOS los endpoints del admin panel sin errores.** 🎉


