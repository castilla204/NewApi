# 🎯 **CAMBIOS PARA FRONTEND: Gestión de Modo Stripe**

## 📋 **RESUMEN**

Se han implementado nuevos endpoints para gestionar el modo Stripe (development/production) desde el panel de administración. Los cambios permiten cambiar entre modo test y producción **sin reiniciar la aplicación**.

---

## 🔗 **NUEVOS ENDPOINTS DISPONIBLES**

### **1. Obtener Modo Actual** ⭐

```http
GET /api/Admin/stripe/mode
Authorization: Bearer {token}
```

**Respuesta (200 OK):**
```json
{
  "mode": "production",
  "changedAt": "2025-01-20T13:45:00Z",
  "changedByUserId": 36
}
```

**Campos:**
- `mode`: `"development"` o `"production"` - Modo actual de Stripe
- `changedAt`: Fecha/hora del último cambio (puede ser `null`)
- `changedByUserId`: ID del usuario que hizo el último cambio (puede ser `null`)

---

### **2. Alternar Modo Automáticamente** ⭐ **RECOMENDADO**

```http
POST /api/Admin/stripe/toggle-mode
Authorization: Bearer {token}
```

**Body:** No requiere body (vacío)

**Respuesta (200 OK):**
```json
{
  "success": true,
  "message": "Modo Stripe cambiado de production a development",
  "previousMode": "production",
  "newMode": "development",
  "warning": "Las claves Stripe se han recargado. Las URLs de webhooks en Stripe Dashboard deben configurarse manualmente para cada modo."
}
```

---

### **3. Establecer Modo Específico**

```http
POST /api/Admin/stripe/mode
Authorization: Bearer {token}
Content-Type: application/json
```

**Body:**
```json
{
  "Mode": "development"
}
```

o

```json
{
  "Mode": "production"
}
```

**Respuesta (200 OK):**
```json
{
  "success": true,
  "message": "Modo Stripe cambiado a development",
  "mode": "development",
  "warning": "Las claves Stripe se han recargado. Las URLs de webhooks en Stripe Dashboard deben configurarse manualmente para cada modo."
}
```

---

## 📝 **TIPOS TYPESCRIPT**

```typescript
// Respuesta de GET /api/Admin/stripe/mode
interface StripeModeResponse {
  mode: 'development' | 'production';
  changedAt: string | null;
  changedByUserId: number | null;
}

// Respuesta de POST /api/Admin/stripe/toggle-mode
interface ToggleStripeModeResponse {
  success: boolean;
  message: string;
  previousMode: string;
  newMode: string;
  warning: string;
}

// Respuesta de POST /api/Admin/stripe/mode
interface SetStripeModeResponse {
  success: boolean;
  message: string;
  mode: 'development' | 'production';
  warning: string;
}

// Request body para POST /api/Admin/stripe/mode
interface SetStripeModeRequest {
  Mode: 'development' | 'production';
}
```

---

## 💻 **EJEMPLOS DE IMPLEMENTACIÓN**

### **Ejemplo 1: Hook Personalizado (React)**

```typescript
import { useState, useEffect } from 'react';

interface StripeModeResponse {
  mode: 'development' | 'production';
  changedAt: string | null;
  changedByUserId: number | null;
}

interface ToggleStripeModeResponse {
  success: boolean;
  message: string;
  previousMode: string;
  newMode: string;
  warning: string;
}

export const useStripeMode = () => {
  const [mode, setMode] = useState<'development' | 'production' | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchMode = async () => {
    try {
      setLoading(true);
      const token = localStorage.getItem('token'); // O tu método de obtener token
      const response = await fetch('/api/Admin/stripe/mode', {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });
      
      if (!response.ok) throw new Error('Error obteniendo modo');
      
      const data: StripeModeResponse = await response.json();
      setMode(data.mode);
      setError(null);
    } catch (err: any) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  const toggleMode = async () => {
    try {
      setLoading(true);
      const token = localStorage.getItem('token');
      const response = await fetch('/api/Admin/stripe/toggle-mode', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      });
      
      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.message || 'Error alternando modo');
      }
      
      const data: ToggleStripeModeResponse = await response.json();
      
      // Mostrar mensaje de éxito
      console.log(data.message);
      
      // Mostrar advertencia sobre webhooks
      console.warn(data.warning);
      
      // Actualizar estado local
      setMode(data.newMode);
      setError(null);
      
      return data;
    } catch (err: any) {
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchMode();
  }, []);

  return { mode, loading, error, fetchMode, toggleMode };
};
```

### **Ejemplo 2: Componente React**

```typescript
import React from 'react';
import { useStripeMode } from './hooks/useStripeMode';

export const StripeModeToggle: React.FC = () => {
  const { mode, loading, error, toggleMode } = useStripeMode();

  const handleToggle = async () => {
    try {
      const result = await toggleMode();
      
      // Mostrar notificación de éxito
      alert(`✅ ${result.message}`);
      
      // Mostrar advertencia sobre webhooks
      alert(`⚠️ ${result.warning}`);
    } catch (error: any) {
      alert(`❌ Error: ${error.message}`);
    }
  };

  if (loading) return <div>Cargando modo Stripe...</div>;
  if (error) return <div>Error: {error}</div>;

  return (
    <div className="stripe-mode-toggle">
      <h3>Modo Stripe Actual: {mode === 'development' ? '🧪 Desarrollo (Test)' : '🚀 Producción (Live)'}</h3>
      <button onClick={handleToggle} disabled={loading}>
        {loading ? 'Cambiando...' : `Cambiar a ${mode === 'development' ? 'Producción' : 'Desarrollo'}`}
      </button>
      <p className="warning">
        ⚠️ Nota: Las URLs de webhooks deben configurarse manualmente en Stripe Dashboard para cada modo.
      </p>
    </div>
  );
};
```

### **Ejemplo 3: Función Simple (Sin Hook)**

```typescript
const getStripeMode = async (): Promise<StripeModeResponse> => {
  const token = localStorage.getItem('token');
  const response = await fetch('/api/Admin/stripe/mode', {
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    }
  });
  
  if (!response.ok) {
    throw new Error('Error obteniendo modo Stripe');
  }
  
  return await response.json();
};

const toggleStripeMode = async (): Promise<ToggleStripeModeResponse> => {
  const token = localStorage.getItem('token');
  const response = await fetch('/api/Admin/stripe/toggle-mode', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    }
  });
  
  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.message || 'Error alternando modo Stripe');
  }
  
  return await response.json();
};

// Uso
const handleToggle = async () => {
  try {
    const result = await toggleStripeMode();
    console.log(result.message);
    console.warn(result.warning);
  } catch (error: any) {
    console.error(error.message);
  }
};
```

---

## ⚠️ **IMPORTANTE**

1. **Solo Administradores**: Estos endpoints requieren rol `Admin`. Si un usuario sin permisos intenta acceder, recibirá `401 Unauthorized`.

2. **Modos Válidos**: Solo se aceptan `"development"` o `"production"`. Cualquier otro valor resultará en `400 Bad Request`.

3. **Cambios Inmediatos**: Los cambios se aplican **inmediatamente** sin necesidad de reiniciar la aplicación backend.

4. **Webhooks**: ⚠️ **IMPORTANTE**: Las URLs de webhooks NO se actualizan automáticamente. Deben configurarse manualmente en Stripe Dashboard:
   - **Modo Test**: https://dashboard.stripe.com/test/webhooks
   - **Modo Live**: https://dashboard.stripe.com/webhooks

---

## 🔄 **FLUJO RECOMENDADO**

1. **Al cargar la página de administración:**
   - Llamar a `GET /api/Admin/stripe/mode` para obtener el modo actual
   - Mostrar el modo en la UI

2. **Al hacer clic en "Alternar Modo":**
   - Llamar a `POST /api/Admin/stripe/toggle-mode`
   - Mostrar mensaje de éxito
   - Mostrar advertencia sobre webhooks
   - Actualizar UI con el nuevo modo

3. **Opcional - Refrescar:**
   - Llamar nuevamente a `GET /api/Admin/stripe/mode` para confirmar el cambio

---

## ✅ **RESUMEN DE ENDPOINTS**

| Método | Endpoint | Body | Uso |
|--------|----------|------|-----|
| `GET` | `/api/Admin/stripe/mode` | - | Obtener modo actual |
| `POST` | `/api/Admin/stripe/toggle-mode` | - | Alternar automáticamente ⭐ |
| `POST` | `/api/Admin/stripe/mode` | `{ "Mode": "..." }` | Establecer modo específico |

**Recomendación:** Usa `toggle-mode` para la mayoría de casos, ya que es más simple y no requiere saber el modo actual.

---

## 🎨 **SUGERENCIAS DE UI**

- Mostrar el modo actual con un badge o indicador visual
- Usar colores diferentes para cada modo (ej: 🟢 Producción, 🟡 Desarrollo)
- Mostrar la fecha del último cambio
- Incluir un tooltip o mensaje explicando qué significa cada modo
- Mostrar advertencia sobre webhooks cuando se cambia el modo

---

## 📚 **DOCUMENTACIÓN ADICIONAL**

Para más detalles sobre la implementación backend, consulta:
- `SOLUCION_CAMBIO_MODO_STRIPE.md` - Documentación técnica completa
- `FRONTEND_STRIPE_MODE_GUIDE.md` - Guía anterior (puede estar desactualizada)

