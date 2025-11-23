# 🎯 **GUÍA FRONTEND: MANEJO DE PendingVerification**

## 📋 **RESUMEN EJECUTIVO**

Con los cambios del backend, **`PendingVerification` ya NO es bloqueante**. El frontend debe:
- ✅ **Permitir todas las operaciones** si `CanCreateServices === true` o `CanReceivePayments === true`
- ✅ **Mostrar un banner informativo** (no bloqueante) cuando `StripeStatus === "PendingVerification"`
- ❌ **NO bloquear la UI** basándose solo en `StripeStatus === "PendingVerification"`

---

## 🔑 **CAMBIOS CLAVE EN EL BACKEND**

### **Antes (Incorrecto):**
```json
{
  "stripeStatus": "PendingVerification",
  "canCreateServices": false,  // ❌ Bloqueaba
  "canReceivePayments": false, // ❌ Bloqueaba
  "statusMessage": "No podrás cobrar hasta que finalice la verificación"
}
```

### **Ahora (Correcto):**
```json
{
  "stripeStatus": "PendingVerification",
  "canCreateServices": true,   // ✅ Permite operar
  "canReceivePayments": true,  // ✅ Permite operar
  "canAccessStripe": true,     // ✅ Permite acceso
  "statusMessage": "🔍 **Verificación de Documentos**: Stripe está revisando la documentación enviada. Puedes seguir operando normalmente mientras se completa la verificación."
}
```

---

## 🎨 **IMPLEMENTACIÓN FRONTEND**

### **1. Lógica de Validación (TypeScript/React)**

```typescript
// ❌ ANTES (Incorrecto):
if (expertStatus.stripeStatus === "PendingVerification") {
  // Bloquear UI
  return <BlockedMessage />;
}

// ✅ AHORA (Correcto):
// Usar los flags del backend, NO el estado directamente
if (!expertStatus.canCreateServices) {
  // Solo bloquear si el backend dice que no puede
  return <BlockedMessage />;
}

// Mostrar banner informativo si está en verificación
if (expertStatus.stripeStatus === "PendingVerification" && expertStatus.canCreateServices) {
  return (
    <>
      <InfoBanner message={expertStatus.statusMessage} />
      {/* Resto de la UI funcional */}
    </>
  );
}
```

---

### **2. Componente de Banner Informativo**

```tsx
// components/StripeStatusBanner.tsx
import React from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Info } from 'lucide-react';

interface StripeStatusBannerProps {
  stripeStatus: string;
  statusMessage: string;
  canCreateServices: boolean;
  canReceivePayments: boolean;
}

export const StripeStatusBanner: React.FC<StripeStatusBannerProps> = ({
  stripeStatus,
  statusMessage,
  canCreateServices,
  canReceivePayments,
}) => {
  // Solo mostrar banner informativo si está en PendingVerification pero puede operar
  if (stripeStatus === "PendingVerification" && (canCreateServices || canReceivePayments)) {
    return (
      <Alert className="mb-4 border-blue-200 bg-blue-50">
        <Info className="h-4 w-4 text-blue-600" />
        <AlertDescription className="text-blue-800">
          <div 
            dangerouslySetInnerHTML={{ 
              __html: statusMessage.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>') 
            }} 
          />
        </AlertDescription>
      </Alert>
    );
  }

  // Para otros estados bloqueantes, mostrar alerta de error
  if (!canCreateServices || !canReceivePayments) {
    return (
      <Alert variant="destructive" className="mb-4">
        <AlertDescription>
          <div 
            dangerouslySetInnerHTML={{ 
              __html: statusMessage.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>') 
            }} 
          />
        </AlertDescription>
      </Alert>
    );
  }

  return null;
};
```

---

### **3. Validación en Formularios**

```tsx
// ❌ ANTES (Incorrecto):
const canCreateService = expertStatus.stripeStatus === "Approved" 
  && expertStatus.onboardingCompleted;

// ✅ AHORA (Correcto):
// Usar directamente el flag del backend
const canCreateService = expertStatus.canCreateServices;

// En el componente:
{expertStatus.canCreateServices ? (
  <CreateServiceForm />
) : (
  <BlockedMessage message={expertStatus.statusMessage} />
)}
```

---

### **4. Página de Panel de Experto**

```tsx
// pages/ExpertPanelPage.tsx
import { useExpertStatus } from '@/hooks/useExpertStatus';
import { StripeStatusBanner } from '@/components/StripeStatusBanner';

export const ExpertPanelPage: React.FC = () => {
  const { data: expertStatus, isLoading } = useExpertStatus();

  if (isLoading) return <LoadingSpinner />;

  return (
    <div className="container mx-auto p-6">
      {/* Banner informativo (no bloqueante) */}
      <StripeStatusBanner
        stripeStatus={expertStatus.stripeStatus}
        statusMessage={expertStatus.statusMessage}
        canCreateServices={expertStatus.canCreateServices}
        canReceivePayments={expertStatus.canReceivePayments}
      />

      {/* Panel de estado de Stripe */}
      <StripeStatusCard status={expertStatus} />

      {/* Funcionalidades disponibles */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mt-6">
        {/* Crear servicio - Usar flag del backend */}
        {expertStatus.canCreateServices ? (
          <CreateServiceCard />
        ) : (
          <DisabledCard 
            title="Crear Servicio"
            message={expertStatus.statusMessage}
          />
        )}

        {/* Recibir pagos - Usar flag del backend */}
        {expertStatus.canReceivePayments ? (
          <PaymentsCard />
        ) : (
          <DisabledCard 
            title="Recibir Pagos"
            message={expertStatus.statusMessage}
          />
        )}
      </div>
    </div>
  );
};
```

---

### **5. Hook Personalizado**

```typescript
// hooks/useExpertStatus.ts
import { useQuery } from '@tanstack/react-query';
import { api } from '@/services/api';

interface ExpertStatus {
  hasStripeAccount: boolean;
  hasPendingOnboarding: boolean;
  onboardingCompleted: boolean;
  stripeStatus: string;
  stripeStatusDetails?: string;
  stripeAccountId?: string;
  canAccessStripe: boolean;
  canCreateServices: boolean;  // ✅ Usar este flag
  canReceivePayments: boolean; // ✅ Usar este flag
  statusMessage: string;
  canRetryOnboarding: boolean;
  rejectionReason?: string;
}

export const useExpertStatus = () => {
  return useQuery<ExpertStatus>({
    queryKey: ['expertStatus'],
    queryFn: async () => {
      const response = await api.get('/Subscription/expert-status');
      return response.data;
    },
    refetchInterval: 30000, // Refrescar cada 30 segundos
  });
};
```

---

### **6. Matriz de Estados y Comportamiento**

| `stripeStatus` | `canCreateServices` | `canReceivePayments` | **Comportamiento Frontend** |
|----------------|---------------------|----------------------|----------------------------|
| `Approved` | `true` | `true` | ✅ Todo funcional, sin banner |
| `PendingVerification` | `true` | `true` | ✅ **Todo funcional + Banner informativo** |
| `PendingVerification` | `false` | `false` | ❌ Bloquear (raro, pero posible) |
| `Pending` | `false` | `false` | ❌ Bloquear, mostrar mensaje |
| `ActionRequired` | `false` | `false` | ❌ Bloquear, mostrar mensaje |
| `RequirementsPastDue` | `false` | `false` | ❌ Bloquear, mostrar mensaje |
| `Rejected` | `false` | `false` | ❌ Bloquear, mostrar mensaje |

---

## 🎯 **REGLAS DE ORO PARA EL FRONTEND**

### ✅ **SIEMPRE HACER:**
1. **Usar `canCreateServices` y `canReceivePayments`** para decidir si bloquear o no
2. **Mostrar banner informativo** cuando `stripeStatus === "PendingVerification"` pero `canCreateServices === true`
3. **Permitir todas las operaciones** si los flags del backend son `true`
4. **Mostrar el `statusMessage` del backend** al usuario

### ❌ **NUNCA HACER:**
1. **NO bloquear** basándose solo en `stripeStatus === "PendingVerification"`
2. **NO asumir** que `PendingVerification` siempre bloquea
3. **NO ignorar** los flags `canCreateServices` y `canReceivePayments`
4. **NO crear lógica duplicada** - confiar en el backend

---

## 📝 **EJEMPLO COMPLETO: Crear Servicio**

```tsx
// components/CreateServiceButton.tsx
import { useExpertStatus } from '@/hooks/useExpertStatus';

export const CreateServiceButton: React.FC = () => {
  const { data: expertStatus } = useExpertStatus();

  // ✅ Usar flag del backend
  if (!expertStatus?.canCreateServices) {
    return (
      <Button disabled variant="outline">
        No puedes crear servicios
      </Button>
    );
  }

  // ✅ Permitir crear servicio incluso si está en PendingVerification
  return (
    <Button onClick={handleCreateService}>
      Crear Nuevo Servicio
    </Button>
  );
};
```

---

## 🔄 **FLUJO COMPLETO**

### **Escenario: Experto en PendingVerification**

1. **Backend devuelve:**
   ```json
   {
     "stripeStatus": "PendingVerification",
     "canCreateServices": true,
     "canReceivePayments": true,
     "statusMessage": "🔍 **Verificación de Documentos**: Stripe está revisando la documentación enviada. Puedes seguir operando normalmente mientras se completa la verificación."
   }
   ```

2. **Frontend muestra:**
   - ✅ Banner informativo (azul, no rojo)
   - ✅ Botón "Crear Servicio" habilitado
   - ✅ Panel de pagos accesible
   - ✅ Todas las funcionalidades operativas

3. **Usuario puede:**
   - ✅ Crear servicios
   - ✅ Recibir pagos
   - ✅ Acceder al panel de Stripe
   - ✅ Operar normalmente

4. **Cuando Stripe complete la verificación:**
   - Backend actualiza a `stripeStatus: "Approved"`
   - Frontend refresca y muestra estado aprobado
   - Banner informativo desaparece

---

## 🎨 **DISEÑO DEL BANNER**

### **Banner Informativo (PendingVerification + canCreateServices: true)**
- **Color:** Azul claro (`bg-blue-50`, `border-blue-200`)
- **Icono:** Info (ℹ️)
- **Tono:** Informativo, no alarmante
- **Mensaje:** "Puedes seguir operando normalmente"

### **Banner de Error (Bloqueado)**
- **Color:** Rojo claro (`bg-red-50`, `border-red-200`)
- **Icono:** Alert (⚠️)
- **Tono:** Alerta, requiere acción
- **Mensaje:** Mensaje del backend explicando por qué está bloqueado

---

## ✅ **CHECKLIST DE IMPLEMENTACIÓN**

- [ ] Actualizar lógica de validación para usar `canCreateServices` y `canReceivePayments`
- [ ] Crear componente `StripeStatusBanner` informativo
- [ ] Actualizar todos los formularios para usar flags del backend
- [ ] Remover bloqueos basados solo en `stripeStatus === "PendingVerification"`
- [ ] Actualizar hook `useExpertStatus` si existe
- [ ] Probar flujo completo con estado `PendingVerification`
- [ ] Verificar que el banner sea informativo, no bloqueante
- [ ] Documentar cambios en el equipo

---

## 🚀 **RESULTADO FINAL**

Con estos cambios, el frontend:
- ✅ **No bloquea innecesariamente** durante la verificación
- ✅ **Mejora la experiencia del usuario** permitiendo operar mientras Stripe revisa
- ✅ **Se alinea con el comportamiento de Stripe** que permite operar durante `pending_verification`
- ✅ **Muestra información clara** al usuario sobre el estado de su cuenta

