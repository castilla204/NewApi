# 🚀 Guía de Disputas Integradas para Frontend

## 🎯 **Solución Integrada - Nuevo Sistema**

Con el nuevo sistema de disputas bidireccional, ahora tienes una solución mucho más integrada y eficiente.

## 📋 **Endpoints Disponibles**

### **1️⃣ `GET /api/dispute/my-disputes` - Endpoint Unificado**
- **Funciona para:** Cliente, Experto y Admin
- **Filtros automáticos:** Solo ves disputas donde participas
- **Incluye:** Respuesta del experto, archivos, fechas límite

### **2️⃣ `GET /api/dispute/{disputeId}/details` - Detalles Específicos**
- **Funciona para:** Cliente, Experto y Admin
- **Seguridad:** Solo puedes ver disputas donde participas

### **3️⃣ `POST /api/dispute/{disputeId}/expert-response` - Respuesta del Experto**
- **Solo para:** Experto involucrado
- **Ventana:** 48 horas desde la creación
- **Archivos:** Puede subir archivos de prueba

## 🔧 **Implementación Frontend Integrada**

### **Hook Actualizado para Disputas**

```typescript
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';

interface DisputeFilters {
  page?: number;
  pageSize?: number;
  searchTerm?: string;
  status?: string;
  searchHireId?: number; // ✅ NUEVO: Filtro por searchHire específico
  reporterId?: number;
  clientId?: number;
  expertId?: number;
  startDate?: string;
  endDate?: string;
  sortBy?: string;
  sortDirection?: 'asc' | 'desc';
}

interface DisputeResponse {
  disputes: DisputeDto[];
  pagination: PaginationMetadata;
  stats?: DisputeStats; // Solo para admin
}

interface DisputeDto {
  id: number;
  searchHireId: number;
  reporterId: number;
  reason: string;
  status: string;
  statusTranslated: string;
  resolutionComments?: string;
  createdAt: string;
  
  // ✅ NUEVOS CAMPOS
  expertResponse?: string;
  expertResponseDeadline?: string;
  expertResponseAt?: string;
  canExpertRespond: boolean;
  
  searchHire: SearchHireInfoDto;
  reporter: UserDto;
  client: UserDto;
  expert?: UserDto;
  search: SearchInfoDto;
  files: DisputeFileDto[];
}

// ✅ HOOK PRINCIPAL ACTUALIZADO
export const useDisputes = () => {
  const queryClient = useQueryClient();

  // ✅ REEMPLAZA useDisputeBySearchHire
  const useDisputesList = (filters: DisputeFilters = {}) => {
    return useQuery({
      queryKey: ['disputes', 'list', filters],
      queryFn: async (): Promise<DisputeResponse> => {
        const params = new URLSearchParams();
        Object.entries(filters).forEach(([key, value]) => {
          if (value !== null && value !== undefined && value !== '') {
            params.append(key, value.toString());
          }
        });

        const response = await fetch(`/api/dispute/my-disputes?${params}`, {
          headers: {
            'Authorization': `Bearer ${localStorage.getItem('token')}`,
            'Content-Type': 'application/json'
          }
        });

        if (!response.ok) {
          throw new Error('Error al obtener disputas');
        }

        return response.json();
      },
      staleTime: 30000, // 30 segundos
    });
  };

  // ✅ NUEVO: Obtener disputa específica
  const useDisputeDetails = (disputeId: number) => {
    return useQuery({
      queryKey: ['disputes', 'details', disputeId],
      queryFn: async (): Promise<DisputeDto> => {
        const response = await fetch(`/api/dispute/${disputeId}/details`, {
          headers: {
            'Authorization': `Bearer ${localStorage.getItem('token')}`,
            'Content-Type': 'application/json'
          }
        });

        if (!response.ok) {
          throw new Error('Error al obtener detalles de la disputa');
        }

        return response.json();
      },
      enabled: !!disputeId,
    });
  };

  // ✅ NUEVO: Respuesta del experto
  const useExpertResponse = () => {
    return useMutation({
      mutationFn: async ({ disputeId, response, files }: {
        disputeId: number;
        response: string;
        files?: File[];
      }) => {
        const formData = new FormData();
        formData.append('Response', response);
        
        if (files) {
          files.forEach(file => {
            formData.append('Files', file);
          });
        }

        const result = await fetch(`/api/dispute/${disputeId}/expert-response`, {
          method: 'POST',
          headers: {
            'Authorization': `Bearer ${localStorage.getItem('token')}`
          },
          body: formData
        });

        if (!result.ok) {
          throw new Error('Error al enviar respuesta del experto');
        }

        return result.json();
      },
      onSuccess: () => {
        queryClient.invalidateQueries({ queryKey: ['disputes'] });
      }
    });
  };

  return {
    useDisputesList,
    useDisputeDetails,
    useExpertResponse
  };
};
```

## 🎯 **Implementación en SearchDetails**

### **Antes (❌ No Integrado)**
```typescript
// ❌ Llamadas separadas
const searchQuery = getSearch(searchId);
const serviceQuery = useServiceByHireId(hireId);
const appointmentQuery = useAppointmentByHireId(hireId);
const deliverablesQuery = useDeliverablesByHireId(hireId);
const disputeQuery = useDisputeBySearchHire(searchQuery.data?.searchHire?.id || 0); // ❌ Separada
```

### **Después (✅ Integrado)**
```typescript
// ✅ Una sola llamada integrada
const SearchDetails = ({ searchId, hireId }: Props) => {
  const searchQuery = getSearch(searchId);
  const serviceQuery = useServiceByHireId(hireId);
  const appointmentQuery = useAppointmentByHireId(hireId);
  const deliverablesQuery = useDeliverablesByHireId(hireId);
  
  // ✅ NUEVA SOLUCIÓN INTEGRADA
  const { useDisputesList } = useDisputes();
  const disputesQuery = useDisputesList({ 
    searchHireId: hireId, // Filtro específico
    page: 1,
    pageSize: 1 // Solo necesitamos una disputa
  });

  // ✅ Acceso directo a la disputa
  const dispute = disputesQuery.data?.disputes?.[0]; // Primera (y única) disputa
  
  // ✅ Información completa disponible
  const hasDispute = !!dispute;
  const disputeStatus = dispute?.status;
  const expertResponse = dispute?.expertResponse;
  const canExpertRespond = dispute?.canExpertRespond;
  const expertResponseDeadline = dispute?.expertResponseDeadline;
  const disputeFiles = dispute?.files || [];

  return (
    <div>
      {/* Tu UI existente */}
      
      {/* ✅ Sección de disputa integrada */}
      {hasDispute && (
        <DisputeSection 
          dispute={dispute}
          canExpertRespond={canExpertRespond}
          expertResponse={expertResponse}
          files={disputeFiles}
        />
      )}
    </div>
  );
};
```

## 🎨 **Componente de Disputa Integrado**

```typescript
interface DisputeSectionProps {
  dispute: DisputeDto;
  canExpertRespond: boolean;
  expertResponse?: string;
  files: DisputeFileDto[];
}

const DisputeSection: React.FC<DisputeSectionProps> = ({
  dispute,
  canExpertRespond,
  expertResponse,
  files
}) => {
  const { useExpertResponse } = useDisputes();
  const expertResponseMutation = useExpertResponse();

  const handleExpertResponse = async (response: string, responseFiles: File[]) => {
    try {
      await expertResponseMutation.mutateAsync({
        disputeId: dispute.id,
        response,
        files: responseFiles
      });
      alert('Respuesta enviada exitosamente');
    } catch (error) {
      alert('Error al enviar respuesta');
    }
  };

  return (
    <div className="dispute-section">
      <h3>Disputa del Servicio</h3>
      
      {/* Información de la disputa */}
      <div className="dispute-info">
        <p><strong>Estado:</strong> {dispute.statusTranslated}</p>
        <p><strong>Razón:</strong> {dispute.reason}</p>
        <p><strong>Fecha:</strong> {new Date(dispute.createdAt).toLocaleDateString()}</p>
      </div>

      {/* Archivos de la disputa */}
      {files.length > 0 && (
        <div className="dispute-files">
          <h4>Archivos Adjuntos</h4>
          {files.map(file => (
            <a key={file.id} href={file.fileUrl} target="_blank" rel="noopener noreferrer">
              {file.fileName}
            </a>
          ))}
        </div>
      )}

      {/* Respuesta del experto */}
      {expertResponse && (
        <div className="expert-response">
          <h4>Respuesta del Experto</h4>
          <p>{expertResponse}</p>
          <p><small>Respondido: {new Date(dispute.expertResponseAt!).toLocaleDateString()}</small></p>
        </div>
      )}

      {/* Formulario de respuesta del experto */}
      {canExpertRespond && (
        <ExpertResponseForm 
          onResponse={handleExpertResponse}
          isLoading={expertResponseMutation.isPending}
          deadline={dispute.expertResponseDeadline}
        />
      )}

      {/* Resolución del admin */}
      {dispute.status === 'Resolved' && dispute.resolutionComments && (
        <div className="admin-resolution">
          <h4>Resolución del Administrador</h4>
          <p>{dispute.resolutionComments}</p>
        </div>
      )}
    </div>
  );
};
```

## 🚀 **Ventajas de la Nueva Solución**

### **1. Más Eficiente**
- ✅ **Una sola llamada** en lugar de dos
- ✅ **Menos requests** al servidor
- ✅ **Mejor rendimiento**

### **2. Mejor Integración**
- ✅ **Mismo patrón** que otras queries
- ✅ **Consistente** con el resto de la app
- ✅ **Fácil de mantener**

### **3. Más Información**
- ✅ **Respuesta del experto** incluida
- ✅ **Archivos** de ambas partes
- ✅ **Fechas límite** y estados
- ✅ **Información completa** en una llamada

### **4. Futuro-Proof**
- ✅ **Escalable** para múltiples disputas
- ✅ **Filtros avanzados** disponibles
- ✅ **Paginación** incluida
- ✅ **Estadísticas** para admin

## 📱 **Migración desde la Solución Anterior**

### **Paso 1: Eliminar código antiguo**
```typescript
// ❌ Eliminar esto:
const disputeQuery = useDisputeBySearchHire(searchQuery.data?.searchHire?.id || 0);
```

### **Paso 2: Agregar nueva implementación**
```typescript
// ✅ Agregar esto:
const { useDisputesList } = useDisputes();
const disputesQuery = useDisputesList({ 
  searchHireId: hireId,
  page: 1,
  pageSize: 1
});
```

### **Paso 3: Actualizar acceso a datos**
```typescript
// ❌ Antes:
const dispute = disputeQuery.data;

// ✅ Después:
const dispute = disputesQuery.data?.disputes?.[0];
```

## 🎯 **Resultado Final**

Con esta nueva implementación tienes:

1. **✅ Una sola llamada** para obtener toda la información de disputas
2. **✅ Mejor integración** con el flujo existente de SearchDetails
3. **✅ Más información** disponible (respuesta del experto, archivos, etc.)
4. **✅ Mejor rendimiento** y menos requests al servidor
5. **✅ Código más limpio** y fácil de mantener

¡La solución está lista para implementar! 🚀





