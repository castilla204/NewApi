# 🎯 GUÍA FRONTEND - ADMIN PANEL: ENDPOINTS Y DTOs COMPLETOS

## ⚠️ PROBLEMA ACTUAL

El frontend está mostrando mal los datos porque:
1. **No está verificando la estructura exacta de las respuestas**
2. **No está manejando campos opcionales (null) correctamente**
3. **No está usando los campos traducidos cuando existen**
4. **No está accediendo a los datos anidados correctamente**

---

## 📋 ÍNDICE DE ENDPOINTS

1. [Usuarios](#1-endpoint-usuarios)
2. [Disputas](#2-endpoint-disputas)
3. [Logs Críticos](#3-endpoint-logs-críticos)
4. [Estados del Sistema](#4-endpoint-estados-del-sistema)
5. [Usuarios Sospechosos](#5-endpoint-usuarios-sospechosos)
6. [Stripe Mode](#6-endpoint-stripe-mode)

---

## 1️⃣ ENDPOINT: USUARIOS

### **GET** `/api/User/all`

**Autenticación:** ✅ Requiere token JWT con rol `Admin`

**Query Parameters:**
```typescript
{
  page?: number;      // Default: 1, mínimo: 1
  pageSize?: number; // Default: 20, rango: 1-50
}
```

**Respuesta Exitosa (200 OK):**
```typescript
{
  users: Array<{
    id: number;
    name: string;
    email: string;
    phoneNumber: string | null;      // ⚠️ PUEDE SER NULL
    phoneVerified: boolean;
    isBlocked: boolean;
    createdAt: string;              // ISO 8601 DateTime
    searchCount: number;           // Número de búsquedas activas
    subscriptionPlan: string;      // Nombre del plan (ej: "Free", "Premium")
    role: string;                   // "Admin" | "User" | "Expert"
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

**Ejemplo de Respuesta:**
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
    },
    {
      "id": 2,
      "name": "María García",
      "email": "maria@example.com",
      "phoneNumber": null,          // ⚠️ NULL - NO TIENE TELÉFONO
      "phoneVerified": false,
      "isBlocked": false,
      "createdAt": "2024-01-20T14:15:00Z",
      "searchCount": 0,
      "subscriptionPlan": "Free",
      "role": "Expert"
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

**⚠️ ERRORES COMUNES EN FRONTEND:**

```typescript
// ❌ INCORRECTO: Asumir que phoneNumber siempre existe
const phone = user.phoneNumber.toUpperCase(); // CRASH si es null

// ✅ CORRECTO: Verificar null
const phone = user.phoneNumber?.toUpperCase() ?? "Sin teléfono";

// ❌ INCORRECTO: No usar paginación
const allUsers = response.users; // Solo muestra 20, no todos

// ✅ CORRECTO: Usar paginación
const { users, pagination } = response;
if (pagination.hasNextPage) {
  // Cargar siguiente página
}
```

---

## 2️⃣ ENDPOINT: DISPUTAS

### **GET** `/api/Dispute/all`

**Autenticación:** ✅ Requiere token JWT con rol `Admin`

**Query Parameters:**
```typescript
{
  page?: number;              // Default: 1
  pageSize?: number;          // Default: 20, máximo: 50
  searchTerm?: string;        // Buscar en razón y comentarios
  status?: string;           // "Pending" | "Resolved" | "Closed"
  reporterId?: number;        // Filtrar por usuario que reportó
  clientId?: number;         // Filtrar por cliente
  expertId?: number;         // Filtrar por experto
  startDate?: string;        // ISO 8601 DateTime
  endDate?: string;          // ISO 8601 DateTime
  sortBy?: string;           // Default: "CreatedAt"
  sortDirection?: string;    // "asc" | "desc", Default: "desc"
}
```

**Respuesta Exitosa (200 OK):**
```typescript
{
  disputes: Array<DisputeDto>;
  pagination: PaginationMetadata;
  stats: DisputeStats;
}
```

### **DisputeDto - Estructura Completa:**

```typescript
interface DisputeDto {
  id: number;
  searchHireId: number;
  reporterId: number;
  reason: string;                    // Razón de la disputa
  status: string;                    // "Pending" | "Resolved" | "Closed"
  statusTranslated: string;           // ⭐ USAR ESTE: "Pendiente" | "Resuelta" | "Cerrada"
  resolutionComments: string | null; // ⚠️ PUEDE SER NULL
  createdAt: string;                 // ISO 8601 DateTime
  
  // ✅ NUEVOS CAMPOS: Respuesta del experto
  expertResponse: string | null;      // ⚠️ PUEDE SER NULL
  expertResponseDeadline: string | null; // ⚠️ PUEDE SER NULL (ISO 8601)
  expertResponseAt: string | null;    // ⚠️ PUEDE SER NULL (ISO 8601)
  canExpertRespond: boolean;          // Si el experto puede aún responder
  
  // ⚠️ OBJETOS ANIDADOS - VERIFICAR SIEMPRE
  searchHire: SearchHireInfoDto;
  reporter: UserDto;
  client: UserDto | null;              // ⚠️ PUEDE SER NULL (usuario eliminado)
  expert: UserDto | null;              // ⚠️ PUEDE SER NULL
  search: SearchInfoDto;
  files: Array<DisputeFileDto>;       // ⚠️ PUEDE ESTAR VACÍO []
}
```

### **SearchHireInfoDto:**
```typescript
interface SearchHireInfoDto {
  id: number;
  status: string;                     // Valor técnico (ej: "AwaitingClientDecision")
  statusTranslated: string;           // ⭐ USAR ESTE: "Esperando Decisión del Cliente"
  amount: number;                     // Decimal (ej: 150.50)
  createdAt: string;                  // ISO 8601 DateTime
}
```

### **UserDto:**
```typescript
interface UserDto {
  id: number;
  name: string;
  email: string;
}
```

### **SearchInfoDto:**
```typescript
interface SearchInfoDto {
  id: number;
  title: string;
  description: string;                // ⚠️ Puede ser string vacío ""
  createdAt: string;                  // ISO 8601 DateTime
}
```

### **DisputeFileDto:**
```typescript
interface DisputeFileDto {
  id: number;
  fileName: string;
  fileType: string;                   // Extensión (ej: "pdf", "jpg")
  fileSize: number;                   // Bytes
  createdAt: string;                  // ISO 8601 DateTime
  filePath: string;                   // URL del archivo (signed URL)
  fileUrl: string;                    // ⭐ Igual que filePath
  uploadedByUserId: number;
  uploadedByUserName: string;
  uploadedByUserEmail: string;
  fileCategory: string;               // "client" | "expert"
  fileCategoryLabel: string;          // ⭐ USAR ESTE: "Archivo del Cliente" | "Archivo del Experto"
}
```

### **DisputeStats:**
```typescript
interface DisputeStats {
  pendingDisputes: number;
  resolvedDisputes: number;
  clientDisputes: number;
  expertDisputes: number;
  thisWeekDisputes: number;
  thisMonthDisputes: number;
}
```

### **PaginationMetadata:**
```typescript
interface PaginationMetadata {
  currentPage: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPrevious: boolean;
  hasNext: boolean;
}
```

**Ejemplo de Respuesta Completa:**
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
        "description": "Necesito que revisen el estado del iPhone antes de comprarlo",
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
    },
    {
      "id": 2,
      "searchHireId": 124,
      "reporterId": 6,
      "reason": "El cliente no respondió",
      "status": "Resolved",
      "statusTranslated": "Resuelta",
      "resolutionComments": "Se reembolsó al cliente",
      "createdAt": "2024-01-18T14:00:00Z",
      "expertResponse": "El cliente no respondió a mis mensajes",
      "expertResponseDeadline": "2024-01-20T14:00:00Z",
      "expertResponseAt": "2024-01-19T16:30:00Z",
      "canExpertRespond": false,
      "searchHire": {
        "id": 124,
        "status": "Cancelled",
        "statusTranslated": "Cancelado",
        "amount": 200.00,
        "createdAt": "2024-01-12T09:00:00Z"
      },
      "reporter": {
        "id": 6,
        "name": "Otro Cliente",
        "email": "otro@example.com"
      },
      "client": {
        "id": 6,
        "name": "Otro Cliente",
        "email": "otro@example.com"
      },
      "expert": null,  // ⚠️ EXPERTO ELIMINADO O NO ASIGNADO
      "search": {
        "id": 51,
        "title": "Revisión de MacBook",
        "description": "",
        "createdAt": "2024-01-11T10:00:00Z"
      },
      "files": []  // ⚠️ SIN ARCHIVOS
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

**⚠️ ERRORES COMUNES EN FRONTEND:**

```typescript
// ❌ INCORRECTO: Usar status en lugar de statusTranslated
<div>{dispute.status}</div> // Muestra "Pending" en lugar de "Pendiente"

// ✅ CORRECTO: Usar statusTranslated
<div>{dispute.statusTranslated}</div> // Muestra "Pendiente"

// ❌ INCORRECTO: Asumir que expert siempre existe
const expertName = dispute.expert.name; // CRASH si es null

// ✅ CORRECTO: Verificar null
const expertName = dispute.expert?.name ?? "Experto no disponible";

// ❌ INCORRECTO: Asumir que files siempre tiene elementos
dispute.files[0].fileName; // CRASH si files está vacío

// ✅ CORRECTO: Verificar array
{dispute.files.length > 0 ? (
  <img src={dispute.files[0].fileUrl} />
) : (
  <span>Sin archivos</span>
)}

// ❌ INCORRECTO: No usar fileCategoryLabel
<div>{dispute.files[0].fileCategory}</div> // Muestra "client"

// ✅ CORRECTO: Usar fileCategoryLabel
<div>{dispute.files[0].fileCategoryLabel}</div> // Muestra "Archivo del Cliente"

// ❌ INCORRECTO: No verificar resolutionComments
<div>{dispute.resolutionComments}</div> // Muestra "null" como texto

// ✅ CORRECTO: Verificar null
{dispute.resolutionComments ? (
  <div>{dispute.resolutionComments}</div>
) : (
  <span>Sin comentarios de resolución</span>
)}
```

---

## 3️⃣ ENDPOINT: LOGS CRÍTICOS

### **GET** `/api/Log/critical`

**Autenticación:** ✅ Requiere token JWT con rol `Admin`

**Query Parameters:**
```typescript
{
  page?: number;      // Default: 1
  pageSize?: number; // Default: 20, máximo: 50
}
```

**Respuesta Exitosa (200 OK):**
```typescript
{
  logs: Array<{
    id: number;
    message: string;
    details: string;
    createdAt: string;              // ISO 8601 DateTime
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

**Ejemplo de Respuesta:**
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
    },
    {
      "id": 2,
      "message": "CRITICAL: Database connection lost",
      "details": "PostgreSQL connection timeout",
      "createdAt": "2024-01-20T14:00:00Z",
      "logType": null,              // ⚠️ NULL
      "user": null,                  // ⚠️ NULL (error del sistema)
      "additionalData": null         // ⚠️ NULL
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

**⚠️ ERRORES COMUNES EN FRONTEND:**

```typescript
// ❌ INCORRECTO: Asumir que user siempre existe
<div>{log.user.email}</div> // CRASH si es null

// ✅ CORRECTO: Verificar null
<div>{log.user?.email ?? "Sistema"}</div>

// ❌ INCORRECTO: Asumir que additionalData siempre existe
const paymentId = log.additionalData.paymentId; // CRASH si es null

// ✅ CORRECTO: Verificar null y tipo
const paymentId = log.additionalData?.paymentId ?? "N/A";
```

---

## 4️⃣ ENDPOINT: ESTADOS DEL SISTEMA

### **GET** `/api/SystemStatus/statuses`

**Autenticación:** ✅ Requiere token JWT (no necesariamente Admin)

**Query Parameters:**
```typescript
{
  statusType?: string; // Filtrar por tipo (ej: "SearchHireStatus")
}
```

**Respuesta Exitosa (200 OK):**
```typescript
Array<{
  id: number;
  statusType: string;        // "SearchHireStatus" | "DisputeStatus" | etc.
  statusName: string;       // Nombre técnico
  statusValue: string;      // Valor técnico (ej: "Pending")
  displayName: string;      // ⭐ USAR ESTE: Nombre para mostrar
  description: string | null;
  sortOrder: number;
  createdAt: string;        // ISO 8601 DateTime
  updatedAt: string;       // ISO 8601 DateTime
}>
```

**Ejemplo de Respuesta:**
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
  },
  {
    "id": 2,
    "statusType": "SearchHireStatus",
    "statusName": "InProgress",
    "statusValue": "InProgress",
    "displayName": "En Progreso",
    "description": "Servicio en ejecución",
    "sortOrder": 2,
    "createdAt": "2024-01-01T00:00:00Z",
    "updatedAt": "2024-01-01T00:00:00Z"
  }
]
```

**⚠️ ERRORES COMUNES EN FRONTEND:**

```typescript
// ❌ INCORRECTO: Usar statusValue o statusName
<div>{status.statusValue}</div> // Muestra "Pending"

// ✅ CORRECTO: Usar displayName
<div>{status.displayName}</div> // Muestra "Pendiente"
```

---

## 5️⃣ ENDPOINT: USUARIOS SOSPECHOSOS

### **GET** `/api/Admin/suspicious-users`

**Autenticación:** ✅ Requiere token JWT con rol `Admin`

**Query Parameters:**
```typescript
{
  minutes?: number;              // Default: 15 (ventana de tiempo)
  minRequestsPerMinute?: number; // Default: 50
  maxFailedAuthAttempts?: number; // Default: 5
}
```

**Respuesta Exitosa (200 OK):**
```typescript
{
  success: boolean;
  timestamp: string;             // ISO 8601 DateTime
  windowMinutes: number;
  suspiciousUsersCount: number;
  suspiciousUsers: Array<{
    userId: number;
    email: string;
    name: string;
    lastActivity: string;        // ISO 8601 DateTime
    suspiciousReasons: string[];  // Array de razones
    riskScore: number;           // 0-100
  }>;
  criteria: {
    minRequestsPerMinute: number;
    maxFailedAuthAttempts: number;
    offHoursDetection: boolean;
  };
}
```

**Ejemplo de Respuesta:**
```json
{
  "success": true,
  "timestamp": "2024-01-20T16:00:00Z",
  "windowMinutes": 15,
  "suspiciousUsersCount": 2,
  "suspiciousUsers": [
    {
      "userId": 50,
      "email": "suspicious@example.com",
      "name": "Usuario Sospechoso",
      "lastActivity": "2024-01-20T15:55:00Z",
      "suspiciousReasons": [
        "High request rate detected",
        "Activity during off-hours"
      ],
      "riskScore": 70
    }
  ],
  "criteria": {
    "minRequestsPerMinute": 50,
    "maxFailedAuthAttempts": 5,
    "offHoursDetection": true
  }
}
```

---

## 6️⃣ ENDPOINT: STRIPE MODE

### **GET** `/api/Admin/stripe/mode`

**Autenticación:** ✅ Requiere token JWT con rol `Admin`

**Respuesta Exitosa (200 OK):**
```typescript
{
  mode: string;                  // "development" | "production"
  changedAt: string | null;      // ⚠️ PUEDE SER NULL (ISO 8601 DateTime)
  changedByUserId: number | null; // ⚠️ PUEDE SER NULL
}
```

**Ejemplo de Respuesta:**
```json
{
  "mode": "development",
  "changedAt": "2024-01-15T10:00:00Z",
  "changedByUserId": 1
}
```

---

## 🔧 CHECKLIST PARA FRONTEND

### ✅ Verificaciones Obligatorias:

1. **✅ Siempre verificar campos opcionales (null):**
   ```typescript
   // ✅ CORRECTO
   const value = data.field ?? "Valor por defecto";
   const name = data.user?.name ?? "Usuario desconocido";
   ```

2. **✅ Usar campos traducidos cuando existan:**
   ```typescript
   // ✅ CORRECTO
   <div>{dispute.statusTranslated}</div>  // En lugar de dispute.status
   <div>{status.displayName}</div>        // En lugar de status.statusValue
   ```

3. **✅ Verificar arrays antes de acceder:**
   ```typescript
   // ✅ CORRECTO
   {files.length > 0 && <img src={files[0].fileUrl} />}
   ```

4. **✅ Manejar paginación correctamente:**
   ```typescript
   // ✅ CORRECTO
   const { data, pagination } = response;
   if (pagination.hasNextPage) {
     // Mostrar botón "Cargar más"
   }
   ```

5. **✅ Validar tipos de datos:**
   ```typescript
   // ✅ CORRECTO
   const amount = typeof dispute.searchHire.amount === 'number' 
     ? dispute.searchHire.amount.toFixed(2) 
     : '0.00';
   ```

6. **✅ Formatear fechas correctamente:**
   ```typescript
   // ✅ CORRECTO
   const date = new Date(dispute.createdAt);
   const formatted = date.toLocaleDateString('es-ES');
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
<div>{dispute.statusTranslated}</div> // Si el backend no devuelve este campo

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

---

## 📝 TIPOS TYPESCRIPT RECOMENDADOS

```typescript
// Tipos para usar en el frontend
interface UserDto {
  id: number;
  name: string;
  email: string;
  phoneNumber: string | null;
  phoneVerified: boolean;
  isBlocked: boolean;
  createdAt: string;
  searchCount: number;
  subscriptionPlan: string;
  role: string;
}

interface DisputeDto {
  id: number;
  searchHireId: number;
  reporterId: number;
  reason: string;
  status: string;
  statusTranslated: string;  // ⭐ USAR ESTE
  resolutionComments: string | null;
  createdAt: string;
  expertResponse: string | null;
  expertResponseDeadline: string | null;
  expertResponseAt: string | null;
  canExpertRespond: boolean;
  searchHire: SearchHireInfoDto;
  reporter: UserDto;
  client: UserDto | null;
  expert: UserDto | null;
  search: SearchInfoDto;
  files: DisputeFileDto[];
}

interface SearchHireInfoDto {
  id: number;
  status: string;
  statusTranslated: string;  // ⭐ USAR ESTE
  amount: number;
  createdAt: string;
}

interface DisputeFileDto {
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
  fileCategory: string;
  fileCategoryLabel: string;  // ⭐ USAR ESTE
}

interface PaginationMetadata {
  currentPage: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  hasPrevious: boolean;
  hasNext: boolean;
}
```

---

## 🎯 RESUMEN FINAL

1. **✅ SIEMPRE verificar campos opcionales (null)**
2. **✅ USAR campos traducidos** (`statusTranslated`, `displayName`, `fileCategoryLabel`)
3. **✅ VERIFICAR arrays antes de acceder** (`files.length > 0`)
4. **✅ MANEJAR paginación correctamente**
5. **✅ VALIDAR tipos de datos** antes de usar
6. **✅ FORMATEAR fechas** correctamente

**Con estos cambios, el frontend mostrará correctamente todos los datos del admin panel.** 🎉

