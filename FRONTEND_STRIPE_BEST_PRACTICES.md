# 🎯 **MEJORES PRÁCTICAS FRONTEND: STRIPE STATUS**

## 📋 **RESPUESTA DIRECTA**

### ✅ **SÍ, solo con `GET /api/Subscription/expert-status` el frontend puede saber TODO**

El backend ya hace toda la lógica compleja y te devuelve **flags booleanos simples** que debes usar directamente.

---

## 🔑 **ENDPOINT PRINCIPAL: `expert-status`**

### **Endpoint:**
```http
GET /api/Subscription/expert-status
Authorization: Bearer {token}
```

### **Respuesta Completa:**
```json
{
  "hasStripeAccount": true,
  "hasPendingOnboarding": false,
  "onboardingCompleted": true,
  "stripeStatus": "PendingVerification",  // ← Solo para mostrar, NO para validar
  "stripeStatusDetails": "...",
  "stripeAccountId": "acct_123...",
  
  // ✅ ESTOS SON LOS FLAGS QUE DEBES USAR:
  "canAccessStripe": true,        // ¿Puede acceder al panel de Stripe?
  "canCreateServices": true,     // ¿Puede crear servicios?
  "canReceivePayments": true,     // ¿Puede recibir pagos?
  
  "statusMessage": "🔍 **Verificación de Documentos**: Stripe está revisando...",
  "canRetryOnboarding": false,
  "rejectionReason": null,
  "stripeFutureRequirements": null,
  "stripeFutureDueAt": null
}
```

---

## 🎯 **REGLA DE ORO: USAR LOS FLAGS, NO EL ESTADO**

### ❌ **INCORRECTO (No hacer esto):**
```typescript
// ❌ NO validar basándose en el estado string
if (expertStatus.stripeStatus === "Approved") {
  // Permitir
}

if (expertStatus.stripeStatus === "PendingVerification") {
  // Bloquear
}
```

### ✅ **CORRECTO (Hacer esto):**
```typescript
// ✅ Usar directamente los flags booleanos
if (expertStatus.canCreateServices) {
  // Permitir crear servicios
}

if (expertStatus.canReceivePayments) {
  // Permitir recibir pagos
}
```

---

## 📊 **MATRIZ DE DECISIÓN SIMPLE**

| **Acción** | **Flag a Usar** | **Ejemplo** |
|------------|-----------------|-------------|
| ¿Mostrar botón "Crear Servicio"? | `canCreateServices` | `{expertStatus.canCreateServices && <CreateServiceButton />}` |
| ¿Permitir recibir pagos? | `canReceivePayments` | `{expertStatus.canReceivePayments && <PaymentsSection />}` |
| ¿Mostrar link al panel de Stripe? | `canAccessStripe` | `{expertStatus.canAccessStripe && <StripeDashboardLink />}` |
| ¿Mostrar mensaje de estado? | `statusMessage` | `<Alert>{expertStatus.statusMessage}</Alert>` |
| ¿Mostrar color/icono? | `stripeStatus` | Solo para UI visual, NO para lógica |

---

## 💡 **IMPLEMENTACIÓN PRÁCTICA**

### **1. Hook Simple (React Query / SWR)**

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
  canAccessStripe: boolean;      // ✅ Usar este
  canCreateServices: boolean;    // ✅ Usar este
  canReceivePayments: boolean;   // ✅ Usar este
  statusMessage: string;
  canRetryOnboarding: boolean;
  rejectionReason?: string;
  stripeFutureRequirements?: string;
  stripeFutureDueAt?: string | null;
}

export const useExpertStatus = () => {
  return useQuery<ExpertStatus>({
    queryKey: ['expertStatus'],
    queryFn: async () => {
      const response = await api.get('/Subscription/expert-status');
      return response.data;
    },
    staleTime: 30000, // Cache por 30 segundos
    refetchInterval: 60000, // Refrescar cada minuto
  });
};
```

---

### **2. Componente de Validación Reutilizable**

```tsx
// components/StripeGate.tsx
import { useExpertStatus } from '@/hooks/useExpertStatus';

interface StripeGateProps {
  action: 'createServices' | 'receivePayments' | 'accessStripe';
  children: React.ReactNode;
  fallback?: React.ReactNode;
}

export const StripeGate: React.FC<StripeGateProps> = ({ 
  action, 
  children, 
  fallback 
}) => {
  const { data: expertStatus, isLoading } = useExpertStatus();

  if (isLoading) {
    return <LoadingSpinner />;
  }

  // ✅ Validar usando los flags del backend
  let canAccess = false;
  switch (action) {
    case 'createServices':
      canAccess = expertStatus?.canCreateServices ?? false;
      break;
    case 'receivePayments':
      canAccess = expertStatus?.canReceivePayments ?? false;
      break;
    case 'accessStripe':
      canAccess = expertStatus?.canAccessStripe ?? false;
      break;
  }

  if (!canAccess) {
    return fallback || (
      <Alert variant="destructive">
        <AlertDescription>
          {expertStatus?.statusMessage || 'No tienes permisos para esta acción'}
        </AlertDescription>
      </Alert>
    );
  }

  return <>{children}</>;
};
```

---

### **3. Uso en Componentes**

```tsx
// pages/ExpertPanelPage.tsx
import { useExpertStatus } from '@/hooks/useExpertStatus';
import { StripeGate } from '@/components/StripeGate';

export const ExpertPanelPage = () => {
  const { data: expertStatus } = useExpertStatus();

  return (
    <div>
      {/* Banner informativo (siempre mostrar si hay mensaje) */}
      {expertStatus?.statusMessage && (
        <Alert className="mb-4">
          <AlertDescription>
            {expertStatus.statusMessage}
          </AlertDescription>
        </Alert>
      )}

      {/* Crear Servicio - Usar StripeGate */}
      <StripeGate action="createServices">
        <CreateServiceForm />
      </StripeGate>

      {/* O validar directamente */}
      {expertStatus?.canCreateServices ? (
        <CreateServiceButton />
      ) : (
        <DisabledButton message={expertStatus?.statusMessage} />
      )}

      {/* Recibir Pagos */}
      {expertStatus?.canReceivePayments && (
        <PaymentsSection />
      )}

      {/* Acceso a Stripe */}
      {expertStatus?.canAccessStripe && (
        <StripeDashboardLink accountId={expertStatus.stripeAccountId} />
      )}
    </div>
  );
};
```

---

## 🎨 **CUÁNDO USAR CADA ENDPOINT**

### **1. `GET /api/Subscription/expert-status`** ⭐ **PRINCIPAL**
- **Cuándo usar:** Siempre, en todas las pantallas del experto
- **Para qué:** Validar permisos, mostrar estado, decidir qué mostrar
- **Frecuencia:** Cache 30s, refrescar cada minuto
- **Uso:** Hook global `useExpertStatus()`

### **2. `GET /api/Subscription/onboarding-status`**
- **Cuándo usar:** Solo en pantalla de onboarding inicial
- **Para qué:** Ver si hay onboarding pendiente
- **Frecuencia:** Una vez al cargar la pantalla
- **Uso:** Menos completo que `expert-status`

### **3. `POST /api/Subscription/sync-stripe-status`**
- **Cuándo usar:** Después de completar onboarding en Stripe
- **Para qué:** Forzar actualización inmediata del estado
- **Frecuencia:** Solo cuando el usuario vuelve de Stripe
- **Uso:** `await syncStripeStatus()` después de redirección

---

## ✅ **CHECKLIST DE IMPLEMENTACIÓN**

### **Setup Inicial:**
- [ ] Crear hook `useExpertStatus()` que llama a `/expert-status`
- [ ] Cachear respuesta por 30 segundos
- [ ] Refrescar automáticamente cada minuto

### **Validaciones:**
- [ ] Usar `canCreateServices` para botón/formulario de crear servicio
- [ ] Usar `canReceivePayments` para sección de pagos
- [ ] Usar `canAccessStripe` para link al panel de Stripe
- [ ] **NO** validar usando `stripeStatus === "..."`

### **UI:**
- [ ] Mostrar `statusMessage` siempre que exista
- [ ] Usar `stripeStatus` solo para colores/iconos visuales
- [ ] Mostrar banner informativo (azul) si `PendingVerification` + flags `true`
- [ ] Mostrar banner de error (rojo) si flags `false`

---

## 🚫 **ERRORES COMUNES A EVITAR**

### ❌ **Error 1: Validar con el estado string**
```typescript
// ❌ MAL
if (expertStatus.stripeStatus === "Approved") {
  // ...
}

// ✅ BIEN
if (expertStatus.canCreateServices) {
  // ...
}
```

### ❌ **Error 2: Lógica duplicada**
```typescript
// ❌ MAL - No recrear la lógica del backend
const canCreate = expertStatus.stripeStatus === "Approved" 
  && expertStatus.onboardingCompleted;

// ✅ BIEN - Usar el flag del backend
const canCreate = expertStatus.canCreateServices;
```

### ❌ **Error 3: Ignorar los flags**
```typescript
// ❌ MAL - Bloquear solo por el estado
if (expertStatus.stripeStatus === "PendingVerification") {
  return <BlockedMessage />;
}

// ✅ BIEN - Usar el flag
if (!expertStatus.canCreateServices) {
  return <BlockedMessage />;
}
```

---

## 📝 **EJEMPLOS COMPLETOS**

### **Ejemplo 1: Botón Crear Servicio**
```tsx
const CreateServiceButton = () => {
  const { data: expertStatus } = useExpertStatus();

  // ✅ Validar con el flag
  if (!expertStatus?.canCreateServices) {
    return (
      <Button disabled>
        {expertStatus?.statusMessage || 'No puedes crear servicios'}
      </Button>
    );
  }

  return (
    <Button onClick={handleCreate}>
      Crear Nuevo Servicio
    </Button>
  );
};
```

### **Ejemplo 2: Sección de Pagos**
```tsx
const PaymentsSection = () => {
  const { data: expertStatus } = useExpertStatus();

  // ✅ Validar con el flag
  if (!expertStatus?.canReceivePayments) {
    return (
      <Alert variant="destructive">
        {expertStatus?.statusMessage}
      </Alert>
    );
  }

  return (
    <div>
      <h2>Pagos</h2>
      {/* Contenido de pagos */}
    </div>
  );
};
```

### **Ejemplo 3: Banner Informativo**
```tsx
const StripeStatusBanner = () => {
  const { data: expertStatus } = useExpertStatus();

  if (!expertStatus?.statusMessage) return null;

  // ✅ Banner informativo si puede operar pero está en verificación
  const isInfo = expertStatus.stripeStatus === "PendingVerification" 
    && expertStatus.canCreateServices;

  return (
    <Alert variant={isInfo ? "default" : "destructive"}>
      <AlertDescription>
        {expertStatus.statusMessage}
      </AlertDescription>
    </Alert>
  );
};
```

---

## 🎯 **RESUMEN FINAL**

### **✅ HACER:**
1. **Usar `GET /api/Subscription/expert-status`** como fuente única de verdad
2. **Validar con `canCreateServices`, `canReceivePayments`, `canAccessStripe`**
3. **Mostrar `statusMessage`** al usuario
4. **Usar `stripeStatus`** solo para UI visual (colores, iconos)

### **❌ NO HACER:**
1. **NO validar** con `stripeStatus === "..."` directamente
2. **NO duplicar** la lógica del backend
3. **NO ignorar** los flags booleanos
4. **NO crear** lógica condicional compleja en el frontend

---

## 🔄 **FLUJO COMPLETO**

```
1. Usuario entra al panel del experto
   ↓
2. Frontend llama a GET /expert-status
   ↓
3. Backend devuelve flags: canCreateServices, canReceivePayments, etc.
   ↓
4. Frontend usa los flags para mostrar/ocultar funcionalidades
   ↓
5. Frontend muestra statusMessage para informar al usuario
   ↓
6. Usuario puede operar si los flags son true
   ↓
7. Frontend refresca cada minuto para obtener estado actualizado
```

---

**✅ Conclusión: El backend ya hace toda la lógica compleja. El frontend solo debe usar los flags booleanos que devuelve `expert-status`.**

