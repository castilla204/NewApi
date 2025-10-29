# 🎯 **GUÍA COMPLETA: IMPLEMENTACIÓN FRONTEND STRIPE CONNECT**

## 📋 **RESUMEN EJECUTIVO**

Esta guía explica cómo implementar correctamente el estado de Stripe Connect en el frontend, incluyendo todas las funciones disponibles, endpoints, y cómo mostrar la información de estado al usuario.

---

## 🔗 **ENDPOINTS DISPONIBLES PARA EL FRONTEND**

### **1. Obtener Estado de Onboarding**
```http
GET /api/Subscription/onboarding-status
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "hasStripeAccount": true,
  "hasPendingOnboarding": false,
  "onboardingCompleted": true,
  "stripeAccountId": "acct_1234567890",
  "stripeStatus": "Approved",
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos.",
  "canAccessStripe": true
}
```

### **2. Obtener Estado Completo del Experto**
```http
GET /api/Subscription/expert-status
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "hasStripeAccount": true,
  "hasPendingOnboarding": false,
  "onboardingCompleted": true,
  "stripeStatus": "Approved",
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos.",
  "stripeAccountId": "acct_1234567890",
  "canAccessStripe": true,
  "canCreateServices": true,
  "canReceivePayments": true,
  "statusMessage": "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos. Ya puedes crear servicios y comenzar a ganar dinero.",
  "canRetryOnboarding": false,
  "rejectionReason": null
}
```

### **3. Sincronizar Estado con Stripe**
```http
POST /api/Subscription/sync-stripe-status
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "hasStripeAccount": true,
  "hasPendingOnboarding": false,
  "onboardingCompleted": true,
  "stripeStatus": "Approved",
  "stripeStatusDetails": "✅ **Cuenta Aprobada**: ¡Excelente! Tu cuenta de pagos está completamente verificada y lista para recibir pagos.",
  "stripeAccountId": "acct_1234567890",
  "canAccessStripe": true,
  "stripeAccountStatus": {
    "chargesEnabled": true,
    "payoutsEnabled": true,
    "detailsSubmitted": true
  }
}
```

### **4. Reiniciar Onboarding**
```http
POST /api/Subscription/restart-onboarding
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "url": "https://connect.stripe.com/setup/c/acct_1234567890",
  "message": "Onboarding restarted successfully"
}
```

### **5. Crear Onboarding Inicial**
```http
POST /api/Subscription/create-expert-onboarding
Authorization: Bearer {token}
```

**Respuesta:**
```json
{
  "url": "https://connect.stripe.com/setup/c/acct_1234567890",
  "message": "Onboarding created successfully"
}
```

---

## 🎨 **IMPLEMENTACIÓN FRONTEND COMPLETA**

### **1. Servicio de API (TypeScript)**

```typescript
// services/stripeService.ts
export interface OnboardingStatus {
  hasStripeAccount: boolean;
  hasPendingOnboarding: boolean;
  onboardingCompleted: boolean;
  stripeAccountId?: string;
  stripeStatus: string;
  stripeStatusDetails?: string;
  canAccessStripe: boolean;
}

export interface ExpertStatus extends OnboardingStatus {
  canCreateServices: boolean;
  canReceivePayments: boolean;
  statusMessage: string;
  canRetryOnboarding: boolean;
  rejectionReason?: string;
}

export interface StripeSyncStatus extends OnboardingStatus {
  stripeAccountStatus: {
    chargesEnabled: boolean;
    payoutsEnabled: boolean;
    detailsSubmitted: boolean;
  };
}

export interface OnboardingResponse {
  url: string;
  message: string;
}

class StripeService {
  private baseUrl = 'http://localhost:7124/api/Subscription';

  async getOnboardingStatus(): Promise<OnboardingStatus> {
    const response = await fetch(`${this.baseUrl}/onboarding-status`, {
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error('Failed to get onboarding status');
    }
    
    return response.json();
  }

  async getExpertStatus(): Promise<ExpertStatus> {
    const response = await fetch(`${this.baseUrl}/expert-status`, {
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error('Failed to get expert status');
    }
    
    return response.json();
  }

  async syncStripeStatus(): Promise<StripeSyncStatus> {
    const response = await fetch(`${this.baseUrl}/sync-stripe-status`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error('Failed to sync Stripe status');
    }
    
    return response.json();
  }

  async restartOnboarding(): Promise<OnboardingResponse> {
    const response = await fetch(`${this.baseUrl}/restart-onboarding`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error('Failed to restart onboarding');
    }
    
    return response.json();
  }

  async createOnboarding(): Promise<OnboardingResponse> {
    const response = await fetch(`${this.baseUrl}/create-expert-onboarding`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${this.getToken()}`,
        'Content-Type': 'application/json'
      }
    });
    
    if (!response.ok) {
      throw new Error('Failed to create onboarding');
    }
    
    return response.json();
  }

  private getToken(): string {
    return localStorage.getItem('token') || '';
  }
}

export const stripeService = new StripeService();
```

### **2. Hook de React para Estado de Stripe**

```typescript
// hooks/useStripeStatus.ts
import { useState, useEffect } from 'react';
import { stripeService, ExpertStatus } from '../services/stripeService';

export const useStripeStatus = () => {
  const [status, setStatus] = useState<ExpertStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchStatus = async () => {
    try {
      setLoading(true);
      setError(null);
      const expertStatus = await stripeService.getExpertStatus();
      setStatus(expertStatus);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  const syncStatus = async () => {
    try {
      setLoading(true);
      setError(null);
      const syncStatus = await stripeService.syncStripeStatus();
      // Convertir a ExpertStatus
      const expertStatus: ExpertStatus = {
        ...syncStatus,
        canCreateServices: syncStatus.canAccessStripe,
        canReceivePayments: syncStatus.canAccessStripe,
        statusMessage: syncStatus.stripeStatusDetails || '',
        canRetryOnboarding: syncStatus.stripeStatus === 'Rejected' || syncStatus.stripeStatus === 'NotRequested',
        rejectionReason: null
      };
      setStatus(expertStatus);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  const restartOnboarding = async () => {
    try {
      setLoading(true);
      setError(null);
      const response = await stripeService.restartOnboarding();
      window.open(response.url, '_blank');
      // Refrescar estado después de un delay
      setTimeout(fetchStatus, 2000);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  const createOnboarding = async () => {
    try {
      setLoading(true);
      setError(null);
      const response = await stripeService.createOnboarding();
      window.open(response.url, '_blank');
      // Refrescar estado después de un delay
      setTimeout(fetchStatus, 2000);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchStatus();
  }, []);

  return {
    status,
    loading,
    error,
    fetchStatus,
    syncStatus,
    restartOnboarding,
    createOnboarding
  };
};
```

### **3. Componente de Estado de Stripe**

```tsx
// components/StripeStatusCard.tsx
import React from 'react';
import { useStripeStatus } from '../hooks/useStripeStatus';

export const StripeStatusCard: React.FC = () => {
  const { status, loading, error, syncStatus, restartOnboarding, createOnboarding } = useStripeStatus();

  if (loading) {
    return (
      <div className="stripe-status-card loading">
        <div className="spinner"></div>
        <p>Cargando estado de pagos...</p>
      </div>
    );
  }

  if (error) {
    return (
      <div className="stripe-status-card error">
        <h3>❌ Error</h3>
        <p>{error}</p>
        <button onClick={syncStatus}>Reintentar</button>
      </div>
    );
  }

  if (!status) {
    return (
      <div className="stripe-status-card error">
        <h3>❌ Error</h3>
        <p>No se pudo cargar el estado de pagos</p>
      </div>
    );
  }

  const getStatusIcon = (stripeStatus: string) => {
    switch (stripeStatus) {
      case 'Approved':
        return '✅';
      case 'Pending':
        return '⏳';
      case 'Rejected':
        return '❌';
      case 'NotRequested':
        return '🔧';
      case 'Deauthorized':
        return '🚫';
      default:
        return '❓';
    }
  };

  const getStatusColor = (stripeStatus: string) => {
    switch (stripeStatus) {
      case 'Approved':
        return '#10B981'; // green
      case 'Pending':
        return '#F59E0B'; // yellow
      case 'Rejected':
        return '#EF4444'; // red
      case 'NotRequested':
        return '#6B7280'; // gray
      case 'Deauthorized':
        return '#DC2626'; // red
      default:
        return '#6B7280'; // gray
    }
  };

  return (
    <div className="stripe-status-card">
      <div className="status-header">
        <h3>
          {getStatusIcon(status.stripeStatus)} Estado de Pagos
        </h3>
        <div 
          className="status-badge"
          style={{ backgroundColor: getStatusColor(status.stripeStatus) }}
        >
          {status.stripeStatus}
        </div>
      </div>

      <div className="status-details">
        <p className="status-message">{status.statusMessage}</p>
        
        {status.stripeStatusDetails && (
          <div className="status-details-text">
            <p>{status.stripeStatusDetails}</p>
          </div>
        )}

        {status.rejectionReason && (
          <div className="rejection-reason">
            <h4>Motivo del rechazo:</h4>
            <p>{status.rejectionReason}</p>
          </div>
        )}
      </div>

      <div className="status-actions">
        {status.stripeStatus === 'NotRequested' && (
          <button 
            className="btn btn-primary"
            onClick={createOnboarding}
          >
            Configurar Pagos
          </button>
        )}

        {status.stripeStatus === 'Rejected' && status.canRetryOnboarding && (
          <button 
            className="btn btn-warning"
            onClick={restartOnboarding}
          >
            Intentar de Nuevo
          </button>
        )}

        {status.stripeStatus === 'Pending' && (
          <button 
            className="btn btn-secondary"
            onClick={syncStatus}
          >
            Sincronizar Estado
          </button>
        )}

        {status.stripeStatus === 'Approved' && (
          <div className="success-actions">
            <p className="success-text">
              ¡Tu cuenta está lista! Puedes crear servicios y recibir pagos.
            </p>
            <button 
              className="btn btn-secondary"
              onClick={syncStatus}
            >
              Sincronizar Estado
            </button>
          </div>
        )}
      </div>

      <div className="status-info">
        <div className="info-item">
          <span className="label">Cuenta Stripe:</span>
          <span className="value">
            {status.hasStripeAccount ? '✅ Configurada' : '❌ No configurada'}
          </span>
        </div>
        <div className="info-item">
          <span className="label">Onboarding:</span>
          <span className="value">
            {status.onboardingCompleted ? '✅ Completado' : '⏳ Pendiente'}
          </span>
        </div>
        <div className="info-item">
          <span className="label">Crear Servicios:</span>
          <span className="value">
            {status.canCreateServices ? '✅ Permitido' : '❌ No permitido'}
          </span>
        </div>
        <div className="info-item">
          <span className="label">Recibir Pagos:</span>
          <span className="value">
            {status.canReceivePayments ? '✅ Permitido' : '❌ No permitido'}
          </span>
        </div>
      </div>
    </div>
  );
};
```

### **4. Estilos CSS**

```css
/* styles/StripeStatusCard.css */
.stripe-status-card {
  background: white;
  border-radius: 12px;
  padding: 24px;
  box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
  border: 1px solid #e5e7eb;
  max-width: 600px;
  margin: 0 auto;
}

.stripe-status-card.loading {
  text-align: center;
  padding: 48px 24px;
}

.stripe-status-card.error {
  border-color: #ef4444;
  background-color: #fef2f2;
}

.status-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
}

.status-header h3 {
  margin: 0;
  font-size: 1.25rem;
  font-weight: 600;
}

.status-badge {
  color: white;
  padding: 4px 12px;
  border-radius: 20px;
  font-size: 0.875rem;
  font-weight: 500;
  text-transform: uppercase;
}

.status-details {
  margin-bottom: 24px;
}

.status-message {
  font-size: 1rem;
  margin-bottom: 12px;
  line-height: 1.5;
}

.status-details-text {
  background-color: #f9fafb;
  padding: 12px;
  border-radius: 8px;
  border-left: 4px solid #3b82f6;
}

.rejection-reason {
  background-color: #fef2f2;
  padding: 12px;
  border-radius: 8px;
  border-left: 4px solid #ef4444;
  margin-top: 12px;
}

.rejection-reason h4 {
  margin: 0 0 8px 0;
  color: #dc2626;
  font-size: 0.875rem;
  font-weight: 600;
}

.status-actions {
  margin-bottom: 24px;
}

.btn {
  padding: 12px 24px;
  border-radius: 8px;
  border: none;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.2s;
  text-decoration: none;
  display: inline-block;
  text-align: center;
}

.btn-primary {
  background-color: #3b82f6;
  color: white;
}

.btn-primary:hover {
  background-color: #2563eb;
}

.btn-warning {
  background-color: #f59e0b;
  color: white;
}

.btn-warning:hover {
  background-color: #d97706;
}

.btn-secondary {
  background-color: #6b7280;
  color: white;
}

.btn-secondary:hover {
  background-color: #4b5563;
}

.success-actions {
  text-align: center;
}

.success-text {
  color: #059669;
  font-weight: 500;
  margin-bottom: 12px;
}

.status-info {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
  padding-top: 16px;
  border-top: 1px solid #e5e7eb;
}

.info-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.label {
  font-weight: 500;
  color: #6b7280;
}

.value {
  font-weight: 600;
  color: #111827;
}

.spinner {
  width: 32px;
  height: 32px;
  border: 3px solid #f3f4f6;
  border-top: 3px solid #3b82f6;
  border-radius: 50%;
  animation: spin 1s linear infinite;
  margin: 0 auto 16px;
}

@keyframes spin {
  0% { transform: rotate(0deg); }
  100% { transform: rotate(360deg); }
}

@media (max-width: 640px) {
  .status-info {
    grid-template-columns: 1fr;
  }
  
  .status-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
  }
}
```

---

## 🎯 **ESTADOS POSIBLES Y CÓMO MANEJARLOS**

### **1. NotRequested (No solicitado)**
- **Icono**: 🔧
- **Color**: Gris
- **Acción**: Mostrar botón "Configurar Pagos"
- **Mensaje**: "No has configurado tu cuenta de pagos de Stripe"

### **2. Pending (Pendiente)**
- **Icono**: ⏳
- **Color**: Amarillo
- **Acción**: Mostrar botón "Sincronizar Estado"
- **Mensaje**: "Tu cuenta de pagos está siendo revisada por Stripe"

### **3. Approved (Aprobado)**
- **Icono**: ✅
- **Color**: Verde
- **Acción**: Mostrar mensaje de éxito
- **Mensaje**: "¡Excelente! Tu cuenta de pagos está completamente verificada"

### **4. Rejected (Rechazado)**
- **Icono**: ❌
- **Color**: Rojo
- **Acción**: Mostrar botón "Intentar de Nuevo"
- **Mensaje**: "Tu solicitud de cuenta de pagos fue rechazada"

### **5. Deauthorized (Desautorizado)**
- **Icono**: 🚫
- **Color**: Rojo
- **Acción**: Contactar soporte
- **Mensaje**: "Tu cuenta de pagos ha sido desautorizada"

---

## 🔄 **FLUJO DE IMPLEMENTACIÓN RECOMENDADO**

### **1. Página de Dashboard del Experto**
```tsx
// pages/ExpertDashboard.tsx
import { StripeStatusCard } from '../components/StripeStatusCard';

export const ExpertDashboard = () => {
  return (
    <div className="dashboard">
      <h1>Mi Dashboard</h1>
      <StripeStatusCard />
      {/* Otros componentes del dashboard */}
    </div>
  );
};
```

### **2. Página de Configuración de Pagos**
```tsx
// pages/PaymentSetup.tsx
import { useStripeStatus } from '../hooks/useStripeStatus';

export const PaymentSetup = () => {
  const { status, createOnboarding, restartOnboarding } = useStripeStatus();

  if (!status) return <div>Cargando...</div>;

  return (
    <div className="payment-setup">
      <h1>Configuración de Pagos</h1>
      <StripeStatusCard />
      
      {status.stripeStatus === 'NotRequested' && (
        <div className="setup-actions">
          <button onClick={createOnboarding}>
            Comenzar Configuración
          </button>
        </div>
      )}
      
      {status.stripeStatus === 'Rejected' && (
        <div className="retry-actions">
          <button onClick={restartOnboarding}>
            Intentar de Nuevo
          </button>
        </div>
      )}
    </div>
  );
};
```

### **3. Verificación en Creación de Servicios**
```tsx
// components/CreateServiceButton.tsx
import { useStripeStatus } from '../hooks/useStripeStatus';

export const CreateServiceButton = () => {
  const { status } = useStripeStatus();

  if (!status?.canCreateServices) {
    return (
      <div className="disabled-button">
        <button disabled>
          Configura tu cuenta de pagos primero
        </button>
        <p>Necesitas tener una cuenta de pagos aprobada para crear servicios</p>
      </div>
    );
  }

  return (
    <button className="create-service-btn">
      Crear Servicio
    </button>
  );
};
```

---

## 🚀 **FUNCIONES ADICIONALES RECOMENDADAS**

### **1. Polling Automático para Estado Pendiente**
```typescript
// hooks/useStripeStatus.ts (adición)
useEffect(() => {
  if (status?.stripeStatus === 'Pending') {
    const interval = setInterval(() => {
      syncStatus();
    }, 30000); // Sincronizar cada 30 segundos

    return () => clearInterval(interval);
  }
}, [status?.stripeStatus]);
```

### **2. Notificaciones Push**
```typescript
// utils/notifications.ts
export const showStripeStatusNotification = (status: ExpertStatus) => {
  if (status.stripeStatus === 'Approved') {
    // Mostrar notificación de éxito
    showNotification('¡Cuenta de pagos aprobada!', 'success');
  } else if (status.stripeStatus === 'Rejected') {
    // Mostrar notificación de rechazo
    showNotification('Cuenta de pagos rechazada', 'error');
  }
};
```

### **3. Validación en Tiempo Real**
```typescript
// utils/validation.ts
export const validateStripeStatus = (status: ExpertStatus): boolean => {
  return status.stripeStatus === 'Approved' && status.onboardingCompleted;
};
```

---

## ✅ **CHECKLIST DE IMPLEMENTACIÓN**

- [ ] ✅ Servicio de API implementado
- [ ] ✅ Hook de React creado
- [ ] ✅ Componente de estado implementado
- [ ] ✅ Estilos CSS aplicados
- [ ] ✅ Manejo de todos los estados
- [ ] ✅ Botones de acción funcionales
- [ ] ✅ Polling para estado pendiente
- [ ] ✅ Notificaciones implementadas
- [ ] ✅ Validaciones en tiempo real
- [ ] ✅ Responsive design
- [ ] ✅ Manejo de errores
- [ ] ✅ Loading states
- [ ] ✅ Testing implementado

---

## 🎉 **RESULTADO FINAL**

Con esta implementación, el frontend tendrá:

1. **✅ Estado completo** de Stripe Connect
2. **✅ Interfaz intuitiva** para el usuario
3. **✅ Acciones apropiadas** para cada estado
4. **✅ Sincronización automática** con Stripe
5. **✅ Manejo robusto** de errores
6. **✅ Experiencia de usuario** optimizada

**¡Tu implementación de Stripe Connect en el frontend estará 100% completa y funcional!** 🚀
