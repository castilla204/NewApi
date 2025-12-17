# 🎯 **GUÍA FRONTEND: GESTIÓN DE MODO STRIPE**

## 📋 **RESUMEN**

Esta guía explica cómo usar los endpoints para gestionar el modo de Stripe (development/production) desde el frontend. Solo los administradores pueden acceder a estos endpoints.

---

## 🔗 **ENDPOINTS DISPONIBLES**

### **1. Obtener Modo Actual** ⭐

```http
GET /api/Admin/stripe/mode
Authorization: Bearer {token}
```

**Respuesta (200 OK):**
```json
{
  "mode": "production",
  "changedAt": "2025-12-03T13:45:00Z",
  "changedByUserId": 36
}
```

**Campos:**
- `mode`: `"development"` o `"production"` - Modo actual de Stripe
- `changedAt`: Fecha/hora del último cambio (puede ser `null`)
- `changedByUserId`: ID del usuario que hizo el último cambio (puede ser `null`)

**Ejemplo TypeScript:**
```typescript
const getStripeMode = async (): Promise<StripeModeResponse> => {
  const response = await fetch('http://localhost:7124/api/Admin/stripe/mode', {
    method: 'GET',
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
```

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
  "message": "Modo Stripe cambiado de production a development",
  "previousMode": "production",
  "newMode": "development",
  "warning": "Se requiere reiniciar la aplicación para aplicar los cambios completamente"
}
```

**Ejemplo TypeScript:**
```typescript
const toggleStripeMode = async (): Promise<ToggleStripeModeResponse> => {
  const response = await fetch('http://localhost:7124/api/Admin/stripe/toggle-mode', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    }
    // No body necesario
  });
  
  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.message || 'Error alternando modo Stripe');
  }
  
  return await response.json();
};
```

**Uso en React:**
```typescript
const handleToggleMode = async () => {
  try {
    const result = await toggleStripeMode();
    
    // Mostrar mensaje de éxito
    toast.success(result.message);
    
    // Mostrar advertencia sobre reinicio
    toast.warning(result.warning);
    
    // Actualizar estado local
    setStripeMode(result.newMode);
    
    // Opcional: Refrescar datos
    await refetchStripeMode();
  } catch (error) {
    toast.error(error.message);
  }
};
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
  "message": "Modo Stripe cambiado a development",
  "mode": "development",
  "warning": "Se requiere reiniciar la aplicación para aplicar los cambios completamente"
}
```

**Ejemplo TypeScript:**
```typescript
const setStripeMode = async (mode: 'development' | 'production'): Promise<SetStripeModeResponse> => {
  const response = await fetch('http://localhost:7124/api/Admin/stripe/mode', {
    method: 'POST',
    headers: {
      'Authorization': `Bearer ${token}`,
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({ Mode: mode })
  });
  
  if (!response.ok) {
    const error = await response.json();
    throw new Error(error.message || 'Error estableciendo modo Stripe');
  }
  
  return await response.json();
};
```

---

## 🎨 **IMPLEMENTACIÓN RECOMENDADA EN REACT**

### **Hook Personalizado:**

```typescript
import { useState, useEffect } from 'react';

interface StripeModeResponse {
  mode: 'development' | 'production';
  changedAt: string | null;
  changedByUserId: number | null;
}

interface ToggleStripeModeResponse {
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
      const response = await fetch('/api/Admin/stripe/mode', {
        headers: {
          'Authorization': `Bearer ${getToken()}`,
        }
      });
      
      if (!response.ok) throw new Error('Error obteniendo modo');
      
      const data: StripeModeResponse = await response.json();
      setMode(data.mode);
      setError(null);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  const toggleMode = async () => {
    try {
      setLoading(true);
      const response = await fetch('/api/Admin/stripe/toggle-mode', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${getToken()}`,
          'Content-Type': 'application/json'
        }
      });
      
      if (!response.ok) {
        const error = await response.json();
        throw new Error(error.message || 'Error alternando modo');
      }
      
      const data: ToggleStripeModeResponse = await response.json();
      setMode(data.newMode);
      
      // Mostrar advertencia
      alert(data.warning);
      
      return data;
    } catch (err) {
      setError(err.message);
      throw err;
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchMode();
  }, []);

  return {
    mode,
    loading,
    error,
    toggleMode,
    refetch: fetchMode
  };
};
```

### **Componente de UI:**

```typescript
import React from 'react';
import { useStripeMode } from './hooks/useStripeMode';

export const StripeModeToggle: React.FC = () => {
  const { mode, loading, toggleMode } = useStripeMode();

  if (loading) {
    return <div>Cargando...</div>;
  }

  return (
    <div className="stripe-mode-toggle">
      <div className="current-mode">
        <span>Modo actual: </span>
        <strong className={mode === 'production' ? 'text-green' : 'text-yellow'}>
          {mode === 'production' ? '🟢 Production' : '🟡 Development'}
        </strong>
      </div>
      
      <button 
        onClick={toggleMode}
        className="toggle-button"
        disabled={loading}
      >
        {mode === 'production' 
          ? 'Cambiar a Development' 
          : 'Cambiar a Production'}
      </button>
      
      <p className="warning-text">
        ⚠️ Se requiere reiniciar la aplicación para aplicar los cambios
      </p>
    </div>
  );
};
```

---

## ⚠️ **IMPORTANTE**

1. **Reinicio Requerido**: Después de cambiar el modo, **se requiere reiniciar la aplicación backend** para que los cambios surtan efecto completamente.

2. **Solo Administradores**: Estos endpoints requieren rol `Admin`. Si un usuario sin permisos intenta acceder, recibirá `401 Unauthorized`.

3. **Modos Válidos**: Solo se aceptan `"development"` o `"production"`. Cualquier otro valor resultará en `400 Bad Request`.

4. **Persistencia**: Los cambios se guardan en la base de datos en la tabla `SystemSettings`.

---

## 🔄 **FLUJO RECOMENDADO**

1. **Al cargar la página de administración:**
   - Llamar a `GET /api/Admin/stripe/mode` para obtener el modo actual
   - Mostrar el modo en la UI

2. **Al hacer clic en "Alternar Modo":**
   - Llamar a `POST /api/Admin/stripe/toggle-mode`
   - Mostrar mensaje de éxito
   - Mostrar advertencia sobre reinicio
   - Actualizar UI con el nuevo modo

3. **Opcional - Refrescar:**
   - Llamar nuevamente a `GET /api/Admin/stripe/mode` para confirmar el cambio

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
  message: string;
  previousMode: string;
  newMode: string;
  warning: string;
}

// Respuesta de POST /api/Admin/stripe/mode
interface SetStripeModeResponse {
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

## ✅ **RESUMEN DE ENDPOINTS**

| Método | Endpoint | Body | Uso |
|--------|----------|------|-----|
| `GET` | `/api/Admin/stripe/mode` | - | Obtener modo actual |
| `POST` | `/api/Admin/stripe/toggle-mode` | - | Alternar automáticamente ⭐ |
| `POST` | `/api/Admin/stripe/mode` | `{ "Mode": "..." }` | Establecer modo específico |

**Recomendación:** Usa `toggle-mode` para la mayoría de casos, ya que es más simple y no requiere saber el modo actual.







