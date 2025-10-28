# 🎨 **GUÍA FRONTEND - SYSTEMSTATUS CON COLORES Y INFORMACIÓN COMPLETA**

## 📋 **RESUMEN DE CAMBIOS**

Se ha implementado un sistema completo de información de estados que incluye:
- **DisplayName**: Nombre legible del estado
- **Description**: Descripción detallada del estado  
- **Color**: Color hexadecimal para UI/UX
- **Información completa**: Todos los metadatos del estado

---

## 🔄 **CAMBIOS EN EL ENDPOINT**

### **Endpoint:** `GET /api/Search/{searchId}/details-complete`

**NUEVA ESTRUCTURA DE RESPUESTA:**

```typescript
interface SearchDetailsCompleteResponse {
  search: {
    searchHire: {
      status: string;                    // ✅ EXISTÍA ANTES
      statusInfo: SystemStatusInfo;     // ✅ NUEVO CAMPO
      // ... otros campos existentes
    }
  };
  appointment: {
    status: string;                     // ✅ EXISTÍA ANTES  
    statusInfo: SystemStatusInfo;       // ✅ NUEVO CAMPO
    // ... otros campos existentes
  };
  // ... otros campos existentes
}
```

---

## 🆕 **NUEVO DTO: SystemStatusInfo**

```typescript
interface SystemStatusInfo {
  id: number;                          // ID único del estado
  statusType: string;                  // Tipo: "SearchHireStatus", "AppointmentStatus"
  statusName: string;                  // Nombre técnico: "Dispute Resolved Client"
  statusValue: string;                 // Valor: "dispute_resolved_client"
  displayName: string;                 // ✅ NUEVO: Nombre legible: "Disputa Resuelta (Cliente)"
  description: string | null;          // ✅ NUEVO: Descripción detallada
  color: string | null;                // ✅ NUEVO: Color hexadecimal: "#17A2B8"
  isActive: boolean;                   // Si el estado está activo
  isFinalizationStatus: boolean;       // Si es estado de finalización
  sortOrder: number;                   // Orden para UI
  createdAt: string;                   // Fecha de creación
  updatedAt: string;                   // Fecha de actualización
}
```

---

## 📊 **EJEMPLO DE RESPUESTA COMPLETA**

```json
{
  "search": {
    "id": 243,
    "userId": 38,
    "title": "revisión presencial",
    "description": "busco camion comercial de mercancias",
    "searchHire": {
      "id": 111,
      "status": "dispute_resolved_client",
      "statusInfo": {
        "id": 15,
        "statusType": "SearchHireStatus",
        "statusName": "Dispute Resolved Client",
        "statusValue": "dispute_resolved_client",
        "displayName": "Disputa Resuelta (Cliente)",
        "description": "La disputa ha sido resuelta a favor del cliente",
        "color": "#17A2B8",
        "isActive": true,
        "isFinalizationStatus": true,
        "sortOrder": 10,
        "createdAt": "2025-09-28T10:00:00Z",
        "updatedAt": "2025-09-28T10:00:00Z"
      },
      "expert": {
        "id": 34,
        "email": "a26865@svalero.com",
        "name": "Diego Castilla Abella",
        "profilePictureUrl": "https://storage.googleapis.com/atrapobucket/experts/fd8c6a6b-08ae-46c4-a25b-16876d298e07.png"
      }
    }
  },
  "appointment": {
    "id": 41,
    "status": "appointment_report_sent",
    "statusInfo": {
      "id": 25,
      "statusType": "AppointmentStatus", 
      "statusName": "Appointment Report Sent",
      "statusValue": "appointment_report_sent",
      "displayName": "Informe Enviado",
      "description": "El experto ha enviado el reporte de la cita",
      "color": "#6610F2",
      "isActive": true,
      "isFinalizationStatus": false,
      "sortOrder": 5,
      "createdAt": "2025-09-28T10:00:00Z",
      "updatedAt": "2025-09-28T10:00:00Z"
    },
    "proposedDate": "2025-10-20T00:00:00Z",
    "proposedTime": "01:51:00",
    "location": "Vía Sin Nombre, 34191 Autilla del Pino, Palencia, España"
  }
}
```

---

## 🎨 **COLORES DISPONIBLES**

| Estado | Color | Descripción |
|--------|-------|-------------|
| `pending` | `#FFA500` | 🟠 Naranja - Pendiente |
| `completed` | `#28A745` | 🟢 Verde - Completado |
| `cancelled` | `#DC3545` | 🔴 Rojo - Cancelado |
| `dispute_resolved_client` | `#17A2B8` | 🔵 Azul - Disputa resuelta |
| `appointment_proposed` | `#6F42C1` | 🟣 Púrpura - Propuesta |
| `appointment_confirmed` | `#20C997` | 🟢 Verde azulado - Confirmado |
| `appointment_rejected` | `#FD7E14` | 🟠 Naranja oscuro - Rechazado |
| `appointment_completed` | `#28A745` | 🟢 Verde - Completado |
| `appointment_cancelled` | `#DC3545` | 🔴 Rojo - Cancelado |
| `appointment_report_sent` | `#6610F2` | 🟣 Púrpura - Reporte enviado |
| `awaiting_appointment` | `#FFC107` | 🟡 Amarillo - Esperando cita |
| `expert_report_timeout` | `#E83E8C` | 🩷 Rosa - Timeout |
| **Por defecto** | `#6C757D` | ⚫ Gris - Estado no definido |

---

## 💻 **IMPLEMENTACIÓN FRONTEND**

### **1. Actualizar Interfaces TypeScript**

```typescript
// Agregar a tus interfaces existentes
interface SearchHireDto {
  id: number;
  status: string;
  statusInfo?: SystemStatusInfo;  // ✅ NUEVO CAMPO
  // ... otros campos existentes
}

interface AppointmentDto {
  id: number;
  status: string;
  statusInfo?: SystemStatusInfo;  // ✅ NUEVO CAMPO
  // ... otros campos existentes
}

interface SystemStatusInfo {
  id: number;
  statusType: string;
  statusName: string;
  statusValue: string;
  displayName: string;
  description: string | null;
  color: string | null;
  isActive: boolean;
  isFinalizationStatus: boolean;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
}
```

### **2. Componente de Estado con Color**

```tsx
interface StatusBadgeProps {
  statusInfo: SystemStatusInfo;
  size?: 'sm' | 'md' | 'lg';
}

const StatusBadge: React.FC<StatusBadgeProps> = ({ statusInfo, size = 'md' }) => {
  const sizeClasses = {
    sm: 'px-2 py-1 text-xs',
    md: 'px-3 py-1.5 text-sm', 
    lg: 'px-4 py-2 text-base'
  };

  return (
    <span
      className={`inline-flex items-center rounded-full font-medium ${sizeClasses[size]}`}
      style={{
        backgroundColor: statusInfo.color ? `${statusInfo.color}20` : '#6C757D20',
        color: statusInfo.color || '#6C757D',
        border: `1px solid ${statusInfo.color || '#6C757D'}40`
      }}
      title={statusInfo.description || statusInfo.displayName}
    >
      {statusInfo.displayName}
    </span>
  );
};
```

### **3. Uso en Componentes**

```tsx
// En tu componente de detalles de búsqueda
const SearchDetails: React.FC<{ search: SearchDetailsCompleteResponse }> = ({ search }) => {
  return (
    <div className="space-y-4">
      {/* Estado de SearchHire */}
      {search.search.searchHire?.statusInfo && (
        <div className="flex items-center gap-2">
          <span className="text-sm text-gray-600">Estado de Contratación:</span>
          <StatusBadge statusInfo={search.search.searchHire.statusInfo} />
        </div>
      )}

      {/* Estado de Appointment */}
      {search.appointment?.statusInfo && (
        <div className="flex items-center gap-2">
          <span className="text-sm text-gray-600">Estado de Cita:</span>
          <StatusBadge statusInfo={search.appointment.statusInfo} />
        </div>
      )}
    </div>
  );
};
```

### **4. Utilidades Helper**

```typescript
// utils/statusUtils.ts
export const getStatusColor = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.color || '#6C757D';
};

export const getStatusDisplayName = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.displayName || statusInfo.statusValue;
};

export const getStatusDescription = (statusInfo: SystemStatusInfo): string => {
  return statusInfo.description || statusInfo.displayName || statusInfo.statusValue;
};

export const isFinalizationStatus = (statusInfo: SystemStatusInfo): boolean => {
  return statusInfo.isFinalizationStatus;
};
```

### **5. CSS Personalizado (Opcional)**

```css
/* Si prefieres usar clases CSS en lugar de estilos inline */
.status-badge {
  @apply inline-flex items-center rounded-full font-medium px-3 py-1.5 text-sm;
}

.status-pending { background-color: #FFA50020; color: #FFA500; border: 1px solid #FFA50040; }
.status-completed { background-color: #28A74520; color: #28A745; border: 1px solid #28A74540; }
.status-cancelled { background-color: #DC354520; color: #DC3545; border: 1px solid #DC354540; }
.status-dispute-resolved { background-color: #17A2B820; color: #17A2B8; border: 1px solid #17A2B840; }
/* ... más clases según necesites */
```

---

## 🔄 **MIGRACIÓN GRADUAL**

### **Opción 1: Compatibilidad Total**
```typescript
// Mantener compatibilidad con código existente
const getStatusDisplay = (searchHire: SearchHireDto): string => {
  // Usar nuevo campo si está disponible, sino usar el anterior
  return searchHire.statusInfo?.displayName || searchHire.status || 'Desconocido';
};
```

### **Opción 2: Migración Completa**
```typescript
// Actualizar todos los usos del campo 'status' por 'statusInfo'
const StatusComponent = ({ statusInfo }: { statusInfo: SystemStatusInfo }) => {
  return (
    <div className="status-container">
      <StatusBadge statusInfo={statusInfo} />
      {statusInfo.description && (
        <p className="text-xs text-gray-500 mt-1">{statusInfo.description}</p>
      )}
    </div>
  );
};
```

---

## 🧪 **TESTING**

### **Casos de Prueba**

```typescript
// Test que el nuevo campo esté presente
describe('SearchDetails API', () => {
  it('should include statusInfo in searchHire', async () => {
    const response = await fetch('/api/Search/243/details-complete');
    const data = await response.json();
    
    expect(data.search.searchHire.statusInfo).toBeDefined();
    expect(data.search.searchHire.statusInfo.displayName).toBeDefined();
    expect(data.search.searchHire.statusInfo.color).toBeDefined();
  });

  it('should include statusInfo in appointment', async () => {
    const response = await fetch('/api/Search/243/details-complete');
    const data = await response.json();
    
    if (data.appointment) {
      expect(data.appointment.statusInfo).toBeDefined();
      expect(data.appointment.statusInfo.displayName).toBeDefined();
      expect(data.appointment.statusInfo.color).toBeDefined();
    }
  });
});
```

---

## 📝 **CHECKLIST DE IMPLEMENTACIÓN**

- [ ] **Actualizar interfaces TypeScript** con `SystemStatusInfo`
- [ ] **Crear componente StatusBadge** con soporte de colores
- [ ] **Actualizar componentes existentes** para usar `statusInfo`
- [ ] **Implementar utilidades helper** para manejo de estados
- [ ] **Agregar CSS personalizado** (opcional)
- [ ] **Escribir tests** para nuevos campos
- [ ] **Actualizar documentación** de componentes
- [ ] **Probar en diferentes estados** de la aplicación

---

## 🚀 **BENEFICIOS**

1. **UX Mejorada**: Estados visuales con colores intuitivos
2. **Información Rica**: Descripciones detalladas de cada estado
3. **Consistencia**: Nombres legibles en lugar de valores técnicos
4. **Flexibilidad**: Fácil agregar nuevos estados y colores
5. **Accesibilidad**: Mejor comprensión del estado actual

---

## ⚠️ **CONSIDERACIONES**

1. **Compatibilidad**: El campo `status` sigue existiendo para compatibilidad
2. **Fallbacks**: Siempre tener valores por defecto si `statusInfo` es null
3. **Performance**: Los nuevos campos no afectan el rendimiento significativamente
4. **Caching**: Considerar cache de colores y nombres de estado

---

*Documentación creada: 2025-01-20*  
*Versión API: Actualizada con SystemStatus completo*
