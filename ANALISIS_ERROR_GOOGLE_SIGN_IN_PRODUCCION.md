# 🔍 Análisis: Error "Google Sign-In no está listo" en Producción

## 🚨 PROBLEMA REPORTADO

En producción, a veces aparece el mensaje:
> **"El inicio de sesión en Google Sign-In no está listo, inténtalo de nuevo en un momento"**

Este error es **intermitente** y ocurre solo en producción, no en desarrollo.

## ⚠️ IMPORTANTE: El problema puede ser del BACKEND también

**Análisis completo disponible en:**
- `ANALISIS_BACKEND_GOOGLE_AUTH_PRODUCCION.md` - Análisis del backend y mejores prácticas
- Este documento - Análisis del frontend

**Resumen:**
- El backend usa `GoogleJsonWebSignature.ValidateAsync` que hace llamadas HTTP a Google
- **NO tiene timeout configurado** - puede tardar hasta 100 segundos
- **NO tiene retry logic** - si falla una vez, falla completamente
- **NO maneja específicamente errores de red** - no distingue entre token inválido y problemas de red
- Esto puede causar que el backend falle y el frontend muestre el error

---

## 🔎 CAUSAS IDENTIFICADAS

### 1. **Script de Google OAuth no se carga a tiempo** ⚠️ CRÍTICO

**Problema:**
- El script de Google (`https://accounts.google.com/gsi/client`) no se carga antes de que React intente renderizar el componente `GoogleLogin`
- En producción, la latencia de red puede ser mayor, causando que el script no esté listo cuando se necesita

**Síntomas:**
- Error aparece aleatoriamente
- Más común en conexiones lentas
- El componente `GoogleLogin` se renderiza antes de que `gapi` esté disponible

**Solución:**
```typescript
// ✅ Verificar que Google OAuth esté listo antes de renderizar
import { useEffect, useState } from 'react';
import { GoogleOAuthProvider, GoogleLogin } from '@react-oauth/google';

function App() {
  const [isGoogleReady, setIsGoogleReady] = useState(false);

  useEffect(() => {
    // Verificar que el script de Google esté cargado
    const checkGoogleScript = () => {
      if (window.google?.accounts?.id) {
        setIsGoogleReady(true);
      } else {
        // Reintentar después de 100ms
        setTimeout(checkGoogleScript, 100);
      }
    };

    // Esperar a que el DOM esté listo
    if (document.readyState === 'complete') {
      checkGoogleScript();
    } else {
      window.addEventListener('load', checkGoogleScript);
    }

    return () => {
      window.removeEventListener('load', checkGoogleScript);
    };
  }, []);

  if (!isGoogleReady) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto mb-4"></div>
          <p className="text-gray-600">Cargando Google Sign-In...</p>
        </div>
      </div>
    );
  }

  return (
    <GoogleOAuthProvider clientId={process.env.REACT_APP_GOOGLE_CLIENT_ID}>
      {/* Tu aplicación aquí */}
    </GoogleOAuthProvider>
  );
}
```

---

### 2. **Content Security Policy (CSP) bloqueando scripts** ⚠️ CRÍTICO

**Problema:**
- El CSP puede estar bloqueando el script de Google OAuth
- En producción, el CSP es más estricto que en desarrollo

**Síntomas:**
- Error en consola: `Refused to load script from 'https://accounts.google.com/gsi/client'`
- El script nunca se carga

**Solución:**
Verificar y actualizar el CSP para incluir los dominios de Google:

```nginx
# Nginx - /etc/nginx/sites-available/inspecciono.com
add_header Content-Security-Policy "
  default-src 'self';
  script-src 'self' 'unsafe-inline' 
    https://accounts.google.com 
    https://apis.google.com 
    https://www.gstatic.com;
  style-src 'self' 'unsafe-inline' 
    https://accounts.google.com 
    https://fonts.googleapis.com;
  img-src 'self' data: https:;
  font-src 'self' data: 
    https://fonts.gstatic.com;
  connect-src 'self' 
    https://api.atrapo.io 
    https://accounts.google.com 
    https://oauth2.googleapis.com;
  frame-src 'self' 
    https://accounts.google.com;
";
```

**Dominios críticos de Google que deben estar permitidos:**
- `https://accounts.google.com` - Scripts y autenticación
- `https://apis.google.com` - APIs de Google
- `https://www.gstatic.com` - Recursos estáticos
- `https://oauth2.googleapis.com` - OAuth endpoints
- `https://fonts.googleapis.com` - Fuentes (opcional)

---

### 3. **GoogleOAuthProvider no envuelve correctamente la app** ⚠️ ALTA

**Problema:**
- El `GoogleOAuthProvider` debe estar en el nivel más alto de la aplicación
- Si está en un componente hijo, puede no inicializarse correctamente

**Solución:**
```typescript
// ✅ CORRECTO: En el nivel raíz (App.tsx o index.tsx)
import { GoogleOAuthProvider } from '@react-oauth/google';

function App() {
  const clientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;
  
  if (!clientId) {
    console.error('❌ REACT_APP_GOOGLE_CLIENT_ID no está configurado');
    return <div>Error de configuración</div>;
  }

  return (
    <GoogleOAuthProvider clientId={clientId}>
      <Router>
        <Routes>
          {/* Tus rutas */}
        </Routes>
      </Router>
    </GoogleOAuthProvider>
  );
}
```

---

### 4. **ClientId no configurado o incorrecto** ⚠️ ALTA

**Problema:**
- El `clientId` no está en las variables de entorno de producción
- El `clientId` es incorrecto o no coincide con el dominio de producción

**Solución:**
1. **Verificar variables de entorno en producción:**
```bash
# En el servidor de producción
echo $REACT_APP_GOOGLE_CLIENT_ID
```

2. **Verificar que el Client ID esté configurado en Google Cloud Console:**
   - Ir a [Google Cloud Console](https://console.cloud.google.com/)
   - APIs & Services → Credentials
   - Verificar que el Client ID tenga autorizado el dominio de producción:
     - `https://inspecciono.com`
     - `https://www.inspecciono.com`

3. **Agregar manejo de errores:**
```typescript
import { GoogleOAuthProvider } from '@react-oauth/google';

function App() {
  const clientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;
  
  useEffect(() => {
    if (!clientId) {
      console.error('❌ REACT_APP_GOOGLE_CLIENT_ID no está configurado');
      // Enviar error a servicio de monitoreo (Sentry, etc.)
    }
  }, []);

  if (!clientId) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <div className="text-center">
          <h1 className="text-2xl font-bold text-red-600 mb-4">
            Error de Configuración
          </h1>
          <p className="text-gray-600">
            Google Sign-In no está configurado correctamente.
            Por favor, contacta al soporte técnico.
          </p>
        </div>
      </div>
    );
  }

  return (
    <GoogleOAuthProvider clientId={clientId}>
      {/* Tu app */}
    </GoogleOAuthProvider>
  );
}
```

---

### 5. **Problemas de red o latencia** ⚠️ MEDIA

**Problema:**
- Conexión lenta o intermitente
- Timeout al cargar el script de Google
- CDN de Google no disponible temporalmente

**Solución:**
```typescript
// ✅ Implementar retry y timeout
import { useEffect, useState } from 'react';

function useGoogleScript() {
  const [isReady, setIsReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryCount, setRetryCount] = useState(0);
  const MAX_RETRIES = 3;

  useEffect(() => {
    let timeoutId: NodeJS.Timeout;
    let retryTimeoutId: NodeJS.Timeout;

    const checkGoogle = () => {
      if (window.google?.accounts?.id) {
        setIsReady(true);
        setError(null);
        return;
      }

      if (retryCount < MAX_RETRIES) {
        retryTimeoutId = setTimeout(() => {
          setRetryCount(prev => prev + 1);
          checkGoogle();
        }, 1000 * (retryCount + 1)); // Backoff exponencial: 1s, 2s, 3s
      } else {
        setError('No se pudo cargar Google Sign-In. Por favor, recarga la página.');
      }
    };

    // Timeout total de 10 segundos
    timeoutId = setTimeout(() => {
      if (!isReady) {
        setError('Timeout al cargar Google Sign-In. Verifica tu conexión a internet.');
      }
    }, 10000);

    checkGoogle();

    return () => {
      clearTimeout(timeoutId);
      clearTimeout(retryTimeoutId);
    };
  }, [retryCount, isReady]);

  return { isReady, error };
}

// Uso:
function LoginPage() {
  const { isReady, error } = useGoogleScript();

  if (error) {
    return (
      <div className="alert alert-warning">
        <p>{error}</p>
        <button onClick={() => window.location.reload()}>
          Recargar Página
        </button>
      </div>
    );
  }

  if (!isReady) {
    return <div>Cargando Google Sign-In...</div>;
  }

  return <GoogleLogin onSuccess={handleSuccess} />;
}
```

---

### 6. **Caché del navegador desactualizado** ⚠️ BAJA

**Problema:**
- El navegador tiene una versión antigua del script de Google en caché
- El script puede estar corrupto o desactualizado

**Solución:**
```typescript
// ✅ Forzar recarga del script si hay problemas
useEffect(() => {
  const script = document.createElement('script');
  script.src = 'https://accounts.google.com/gsi/client';
  script.async = true;
  script.defer = true;
  script.crossOrigin = 'anonymous';
  
  // Agregar timestamp para evitar caché (solo en desarrollo/debug)
  if (process.env.NODE_ENV === 'development') {
    script.src += `?t=${Date.now()}`;
  }

  script.onerror = () => {
    console.error('❌ Error al cargar script de Google OAuth');
    // Reintentar después de 2 segundos
    setTimeout(() => {
      document.head.appendChild(script);
    }, 2000);
  };

  document.head.appendChild(script);

  return () => {
    // Limpiar script al desmontar (opcional)
    const existingScript = document.querySelector('script[src*="accounts.google.com/gsi/client"]');
    if (existingScript) {
      existingScript.remove();
    }
  };
}, []);
```

---

## ✅ SOLUCIÓN COMPLETA RECOMENDADA

### Paso 1: Crear hook personalizado para verificar Google OAuth

```typescript
// hooks/useGoogleOAuth.ts
import { useEffect, useState } from 'react';

interface UseGoogleOAuthReturn {
  isReady: boolean;
  error: string | null;
  retry: () => void;
}

export function useGoogleOAuth(): UseGoogleOAuthReturn {
  const [isReady, setIsReady] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [retryCount, setRetryCount] = useState(0);
  const MAX_RETRIES = 3;
  const TIMEOUT_MS = 10000;

  const checkGoogle = () => {
    if (window.google?.accounts?.id) {
      setIsReady(true);
      setError(null);
      return true;
    }
    return false;
  };

  const retry = () => {
    setRetryCount(0);
    setIsReady(false);
    setError(null);
    checkGoogle();
  };

  useEffect(() => {
    // Verificar si ya está cargado
    if (checkGoogle()) {
      return;
    }

    let timeoutId: NodeJS.Timeout;
    let retryTimeoutId: NodeJS.Timeout;
    let intervalId: NodeJS.Timeout;

    // Verificar periódicamente
    intervalId = setInterval(() => {
      if (checkGoogle()) {
        clearInterval(intervalId);
        clearTimeout(timeoutId);
        if (retryTimeoutId) clearTimeout(retryTimeoutId);
      } else if (retryCount < MAX_RETRIES) {
        // Reintentar con backoff exponencial
        retryTimeoutId = setTimeout(() => {
          setRetryCount(prev => prev + 1);
        }, 1000 * (retryCount + 1));
      }
    }, 100);

    // Timeout total
    timeoutId = setTimeout(() => {
      if (!isReady) {
        setError('El inicio de sesión con Google no está disponible en este momento. Por favor, intenta recargar la página.');
        clearInterval(intervalId);
      }
    }, TIMEOUT_MS);

    return () => {
      clearTimeout(timeoutId);
      clearTimeout(retryTimeoutId);
      clearInterval(intervalId);
    };
  }, [retryCount, isReady]);

  return { isReady, error, retry };
}
```

### Paso 2: Usar el hook en el componente de login

```typescript
// components/LoginPage.tsx
import { GoogleOAuthProvider, GoogleLogin } from '@react-oauth/google';
import { useGoogleOAuth } from '../hooks/useGoogleOAuth';

function LoginContent() {
  const { isReady, error, retry } = useGoogleOAuth();

  if (error) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8">
          <div className="text-center">
            <div className="mb-4">
              <svg className="mx-auto h-12 w-12 text-yellow-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
            </div>
            <h2 className="text-xl font-semibold text-gray-900 mb-2">
              Google Sign-In no disponible
            </h2>
            <p className="text-gray-600 mb-6">{error}</p>
            <div className="space-y-2">
              <button
                onClick={retry}
                className="w-full bg-blue-600 text-white py-2 px-4 rounded-md hover:bg-blue-700 transition"
              >
                Intentar de nuevo
              </button>
              <button
                onClick={() => window.location.reload()}
                className="w-full bg-gray-200 text-gray-800 py-2 px-4 rounded-md hover:bg-gray-300 transition"
              >
                Recargar página
              </button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  if (!isReady) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto mb-4"></div>
          <p className="text-gray-600">Cargando Google Sign-In...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8">
        <h1 className="text-3xl font-bold text-center mb-6">Iniciar Sesión</h1>
        <div className="flex justify-center">
          <GoogleLogin
            onSuccess={handleGoogleSuccess}
            onError={() => {
              console.error('Error en Google Login');
              // Manejar error
            }}
          />
        </div>
      </div>
    </div>
  );
}

export function LoginPage() {
  const clientId = process.env.REACT_APP_GOOGLE_CLIENT_ID;

  if (!clientId) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <div className="text-center text-red-600">
          <h1 className="text-2xl font-bold mb-4">Error de Configuración</h1>
          <p>Google Client ID no está configurado</p>
        </div>
      </div>
    );
  }

  return (
    <GoogleOAuthProvider clientId={clientId}>
      <LoginContent />
    </GoogleOAuthProvider>
  );
}
```

### Paso 3: Verificar CSP en producción

```bash
# Verificar headers CSP
curl -I https://inspecciono.com | grep -i "content-security-policy"
```

Asegurarse de que incluya:
- `script-src ... https://accounts.google.com https://apis.google.com`
- `connect-src ... https://accounts.google.com https://oauth2.googleapis.com`
- `frame-src ... https://accounts.google.com`

---

## 🔍 DIAGNÓSTICO EN PRODUCCIÓN

### Verificar en la consola del navegador:

1. **Abrir DevTools (F12)**
2. **Ir a la pestaña Console**
3. **Buscar errores:**
   - `Refused to load script` → Problema de CSP
   - `GoogleOAuthProvider clientId is required` → ClientId no configurado
   - `gapi is not defined` → Script no cargado

4. **Ir a la pestaña Network:**
   - Buscar `gsi/client` en las peticiones
   - Verificar que el status sea `200 OK`
   - Si es `blocked` o `failed` → Problema de CSP o red

### Verificar variables de entorno:

```bash
# En el servidor de producción
echo $REACT_APP_GOOGLE_CLIENT_ID

# O en el build
grep -r "GOOGLE_CLIENT_ID" .env*
```

---

## 📊 MONITOREO Y LOGGING

Agregar logging para detectar el problema:

```typescript
// utils/googleAuthLogger.ts
export function logGoogleAuthError(error: string, context: any = {}) {
  const logData = {
    error,
    timestamp: new Date().toISOString(),
    userAgent: navigator.userAgent,
    url: window.location.href,
    ...context
  };

  // Enviar a servicio de monitoreo (Sentry, LogRocket, etc.)
  if (window.Sentry) {
    window.Sentry.captureMessage('Google Auth Error', {
      level: 'warning',
      extra: logData
    });
  }

  // También loggear en consola para debugging
  console.error('🔴 Google Auth Error:', logData);
}

// Uso:
useEffect(() => {
  if (error) {
    logGoogleAuthError(error, {
      retryCount,
      isReady,
      hasGoogleScript: !!window.google
    });
  }
}, [error]);
```

---

## ✅ CHECKLIST DE IMPLEMENTACIÓN

- [ ] Verificar que `REACT_APP_GOOGLE_CLIENT_ID` esté configurado en producción
- [ ] Verificar que el Client ID tenga autorizado el dominio de producción en Google Cloud Console
- [ ] Actualizar CSP para incluir todos los dominios de Google necesarios
- [ ] Implementar hook `useGoogleOAuth` para verificar que Google esté listo
- [ ] Agregar manejo de errores y mensajes de usuario amigables
- [ ] Implementar retry con backoff exponencial
- [ ] Agregar logging/monitoreo para detectar el problema
- [ ] Probar en diferentes navegadores y conexiones
- [ ] Probar con conexión lenta (throttling en DevTools)

---

## 📞 CONTACTO

Si el problema persiste después de implementar estas soluciones, verificar:
1. Logs del servidor de producción
2. Logs de Google Cloud Console
3. Estado de los servicios de Google: https://status.cloud.google.com/

---

**Última actualización:** 2025-01-XX  
**Aplicable a:** Frontend de inspecciono.com en producción
