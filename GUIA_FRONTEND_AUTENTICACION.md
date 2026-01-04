# 🔐 GUÍA FRONTEND - SISTEMA DE AUTENTICACIÓN Y TOKENS

## 📋 RESUMEN DEL PROBLEMA

**Problema actual:** Cada vez que recargas la página, se pierde la sesión y el usuario tiene que volver a hacer login.

**Causa:** El frontend no está:
1. Guardando los tokens en localStorage al hacer login
2. Verificando si hay tokens guardados al cargar la app
3. Renovando automáticamente los tokens cuando expiran

**Solución:** Implementar un sistema completo de gestión de tokens que persista la sesión.

---

## 🔑 CÓMO FUNCIONA EL SISTEMA DE TOKENS

### 1. **Access Token (JWT)**
- **Duración:** 1 hora (60 minutos)
- **Uso:** Se envía en cada petición al backend en el header `Authorization: Bearer {accessToken}`
- **Expira:** Después de 1 hora, el backend rechazará las peticiones con 401 Unauthorized

### 2. **Refresh Token**
- **Duración:** 30 días
- **Uso:** Se usa para obtener un nuevo Access Token cuando el actual expira
- **Seguridad:** Se guarda en la base de datos y se puede revocar

### 3. **Formato de respuesta del login**
El backend devuelve los tokens **combinados** separados por `|`:
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...|aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890...",
  "user": {
    "id": 1,
    "name": "Diego Castilla Abella",
    "email": "dcastillaa@gmail.com",
    "phoneVerified": false,
    "role": "Admin"
  },
  "requestId": "guid-uuid"
}
```

**⚠️ IMPORTANTE:** 
- El campo es `token` (no `success`)
- El formato del token es: `{accessToken}|{refreshToken}`
- Siempre separa con `split('|')` para obtener ambos tokens

---

## ✅ SOLUCIÓN COMPLETA PARA EL FRONTEND

### PASO 1: Guardar tokens al hacer login

```typescript
// En tu servicio de autenticación (authService.ts o similar)
async function handleGoogleAuth(googleToken: string) {
  try {
    const response = await fetch('http://localhost:7124/api/User/google-auth', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        accessToken: googleToken
      })
    });

    const data = await response.json();

    // ✅ IMPORTANTE: El backend devuelve { token, user, requestId }
    // NO devuelve { success: true }, solo verifica que exista data.token
    if (data.token) {
      // ✅ CRÍTICO: El backend devuelve los tokens COMBINADOS separados por "|"
      // Formato: "accessToken|refreshToken"
      const [accessToken, refreshToken] = data.token.split('|');

      if (!accessToken || !refreshToken) {
        throw new Error('Formato de token inválido');
      }

      // ✅ PASO 1: Guardar ambos tokens en localStorage
      // Estos tokens son lo que mantiene la sesión activa
      localStorage.setItem('accessToken', accessToken);
      localStorage.setItem('refreshToken', refreshToken);
      
      // ✅ PASO 2: Guardar la fecha de expiración del access token
      // El access token dura 1 hora (60 minutos)
      const expiresAt = new Date(Date.now() + 60 * 60 * 1000).toISOString();
      localStorage.setItem('accessTokenExpiresAt', expiresAt);

      // ✅ PASO 3: Guardar información del usuario
      // Esto te permite saber quién está logueado sin hacer peticiones al backend
      localStorage.setItem('user', JSON.stringify(data.user));

      console.log('✅ Login exitoso - Tokens guardados en localStorage');
      console.log('✅ Usuario:', data.user);

      return { success: true, user: data.user };
    }

    throw new Error('Login failed');
  } catch (error) {
    console.error('Error en autenticación:', error);
    throw error;
  }
}
```

---

### PASO 2: Verificar sesión al cargar la app (CRÍTICO - Esto es lo que falta)

**⚠️ ESTO ES LO MÁS IMPORTANTE:** Cada vez que la app se carga (recarga de página, navegación, etc.), debes verificar si hay tokens guardados.

```typescript
// En tu App.tsx o componente principal
import { useEffect, useState } from 'react';

function App() {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [user, setUser] = useState(null);

  useEffect(() => {
    // ✅ CRÍTICO: Verificar autenticación al cargar la app
    checkAuthStatus();
  }, []);

  async function checkAuthStatus() {
    try {
      // ✅ PASO 1: Leer tokens del localStorage
      const accessToken = localStorage.getItem('accessToken');
      const refreshToken = localStorage.getItem('refreshToken');
      const expiresAt = localStorage.getItem('accessTokenExpiresAt');
      const userData = localStorage.getItem('user');

      // ✅ PASO 2: Si NO hay tokens, el usuario NO está logueado
      if (!accessToken || !refreshToken) {
        console.log('❌ No hay tokens guardados - Usuario no autenticado');
        setIsAuthenticated(false);
        setUser(null);
        setIsLoading(false);
        return;
      }

      // ✅ PASO 3: Verificar si el access token expiró
      if (expiresAt) {
        const expirationDate = new Date(expiresAt);
        const now = new Date();
        
        if (expirationDate < now) {
          // Token expirado, intentar renovar
          console.log('🔄 Access token expirado, renovando...');
          const renewed = await refreshAccessToken();
          
          if (renewed) {
            // ✅ Renovación exitosa
            console.log('✅ Token renovado exitosamente');
            setIsAuthenticated(true);
            if (userData) {
              setUser(JSON.parse(userData));
            }
          } else {
            // ❌ No se pudo renovar (refresh token expirado o inválido)
            console.log('❌ No se pudo renovar token - Redirigiendo a login');
            localStorage.clear();
            setIsAuthenticated(false);
            setUser(null);
            // Opcional: redirigir a login
            // window.location.href = '/login';
          }
        } else {
          // ✅ Token aún válido
          console.log('✅ Token válido - Usuario autenticado');
          setIsAuthenticated(true);
          if (userData) {
            setUser(JSON.parse(userData));
          }
        }
      } else {
        // No hay fecha de expiración guardada, asumir que está válido
        setIsAuthenticated(true);
        if (userData) {
          setUser(JSON.parse(userData));
        }
      }
    } catch (error) {
      console.error('❌ Error verificando autenticación:', error);
      localStorage.clear();
      setIsAuthenticated(false);
      setUser(null);
    } finally {
      setIsLoading(false);
    }
  }

  // Renderizar según el estado de autenticación
  if (isLoading) {
    return <div>Cargando...</div>;
  }

  if (!isAuthenticated) {
    return <LoginPage onLoginSuccess={checkAuthStatus} />;
  }

  return (
    <div>
      <h1>Bienvenido, {user?.name}</h1>
      {/* Tu app aquí */}
    </div>
  );
}
```

**🔍 Cómo saber si el usuario está logueado:**

1. **Verificar tokens en localStorage:**
   ```typescript
   const accessToken = localStorage.getItem('accessToken');
   const refreshToken = localStorage.getItem('refreshToken');
   
   if (accessToken && refreshToken) {
     // ✅ Hay tokens = Usuario está logueado (o debería estarlo)
   } else {
     // ❌ No hay tokens = Usuario NO está logueado
   }
   ```

2. **Verificar expiración del access token:**
   ```typescript
   const expiresAt = localStorage.getItem('accessTokenExpiresAt');
   if (expiresAt && new Date(expiresAt) < new Date()) {
     // Token expirado, necesita renovación
   }
   ```

3. **Verificar datos del usuario:**
   ```typescript
   const userData = localStorage.getItem('user');
   if (userData) {
     const user = JSON.parse(userData);
     // Tienes información del usuario
   }
   ```

---

### PASO 3: Configurar interceptor de Axios/Fetch para renovar tokens automáticamente

```typescript
// En tu archivo de configuración de API (api.ts o similar)
import axios from 'axios';

// ✅ Configurar el token en cada petición
axios.interceptors.request.use(
  (config) => {
    const accessToken = localStorage.getItem('accessToken');
    if (accessToken) {
      config.headers.Authorization = `Bearer ${accessToken}`;
    }
    return config;
  },
  (error) => Promise.reject(error)
);

// ✅ Renovar automáticamente si el token expiró
let isRefreshing = false;
let failedQueue: Array<{
  resolve: (value?: any) => void;
  reject: (reason?: any) => void;
}> = [];

const processQueue = (error: any, token: string | null = null) => {
  failedQueue.forEach((prom) => {
    if (error) {
      prom.reject(error);
    } else {
      prom.resolve(token);
    }
  });
  failedQueue = [];
};

axios.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config;

    // Si es 401 y no hemos reintentado aún
    if (error.response?.status === 401 && !originalRequest._retry) {
      // Si ya estamos refrescando, esperar en la cola
      if (isRefreshing) {
        return new Promise((resolve, reject) => {
          failedQueue.push({ resolve, reject });
        })
          .then((token) => {
            originalRequest.headers.Authorization = `Bearer ${token}`;
            return axios(originalRequest);
          })
          .catch((err) => {
            return Promise.reject(err);
          });
      }

      originalRequest._retry = true;
      isRefreshing = true;

      try {
        const refreshToken = localStorage.getItem('refreshToken');
        
        if (!refreshToken) {
          // No hay refresh token, limpiar y redirigir a login
          localStorage.clear();
          window.location.href = '/login';
          return Promise.reject(error);
        }

        // ✅ Renovar tokens
        const response = await axios.post(
          'http://localhost:7124/api/Auth/refresh-token',
          { refreshToken }
        );

        const { accessToken, refreshToken: newRefreshToken, accessTokenExpiresAt } = response.data;

        // ✅ Guardar nuevos tokens
        localStorage.setItem('accessToken', accessToken);
        localStorage.setItem('refreshToken', newRefreshToken);
        localStorage.setItem('accessTokenExpiresAt', accessTokenExpiresAt);

        // ✅ Actualizar el header de la petición original
        originalRequest.headers.Authorization = `Bearer ${accessToken}`;

        processQueue(null, accessToken);

        // ✅ Reintentar la petición original
        return axios(originalRequest);
      } catch (refreshError) {
        // Error al renovar, limpiar y redirigir a login
        processQueue(refreshError, null);
        localStorage.clear();
        window.location.href = '/login';
        return Promise.reject(refreshError);
      } finally {
        isRefreshing = false;
      }
    }

    return Promise.reject(error);
  }
);
```

---

### PASO 4: Función para renovar tokens manualmente

```typescript
// Función auxiliar para renovar tokens
async function refreshAccessToken(): Promise<boolean> {
  try {
    const refreshToken = localStorage.getItem('refreshToken');
    
    if (!refreshToken) {
      return false;
    }

    const response = await fetch('http://localhost:7124/api/Auth/refresh-token', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({ refreshToken })
    });

    if (!response.ok) {
      return false;
    }

    const data = await response.json();

    // ✅ Guardar nuevos tokens
    localStorage.setItem('accessToken', data.accessToken);
    localStorage.setItem('refreshToken', data.refreshToken);
    localStorage.setItem('accessTokenExpiresAt', data.accessTokenExpiresAt);

    return true;
  } catch (error) {
    console.error('Error renovando token:', error);
    return false;
  }
}
```

---

### PASO 5: Renovar tokens proactivamente (antes de que expiren)

```typescript
// Renovar tokens 5 minutos antes de que expiren
function setupTokenRefresh() {
  setInterval(async () => {
    const expiresAt = localStorage.getItem('accessTokenExpiresAt');
    
    if (!expiresAt) return;

    const expirationTime = new Date(expiresAt).getTime();
    const now = Date.now();
    const timeUntilExpiration = expirationTime - now;

    // Si quedan menos de 5 minutos, renovar
    if (timeUntilExpiration < 5 * 60 * 1000 && timeUntilExpiration > 0) {
      console.log('🔄 Renovando token proactivamente...');
      await refreshAccessToken();
    }
  }, 60000); // Verificar cada minuto
}

// Llamar al iniciar la app
setupTokenRefresh();
```

---

### PASO 6: Logout

```typescript
async function logout() {
  try {
    const refreshToken = localStorage.getItem('refreshToken');
    const accessToken = localStorage.getItem('accessToken');

    // ✅ Revocar el refresh token en el backend
    if (refreshToken && accessToken) {
      await fetch('http://localhost:7124/api/Auth/logout', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${accessToken}`
        },
        body: JSON.stringify({ refreshToken })
      });
    }
  } catch (error) {
    console.error('Error en logout:', error);
  } finally {
    // ✅ Limpiar localStorage
    localStorage.clear();
    
    // ✅ Redirigir a login
    window.location.href = '/login';
  }
}
```

---

## 📝 RESUMEN DE CAMBIOS NECESARIOS

### ✅ Lo que DEBES hacer:

1. **Al hacer login:**
   - Separar el token combinado: `token.split('|')`
   - Guardar `accessToken` y `refreshToken` en localStorage
   - Guardar `accessTokenExpiresAt` (1 hora desde ahora)

2. **Al cargar la app:**
   - Verificar si hay tokens en localStorage
   - Si el access token expiró, renovarlo automáticamente
   - Si no se puede renovar, limpiar y redirigir a login

3. **En cada petición:**
   - Enviar el access token en el header: `Authorization: Bearer {accessToken}`
   - Si recibes 401, renovar automáticamente con el refresh token

4. **Renovación proactiva:**
   - Renovar tokens 5 minutos antes de que expiren

5. **Al hacer logout:**
   - Revocar el refresh token en el backend
   - Limpiar localStorage

---

## 🔍 ENDPOINTS DEL BACKEND

### Login con Google
```
POST /api/User/google-auth
Body: { "accessToken": "google_jwt_token" }
Response: { 
  "token": "accessToken|refreshToken",
  "user": {
    "id": 1,
    "name": "Nombre Usuario",
    "email": "usuario@example.com",
    "phoneVerified": false,
    "role": "Client" | "Expert" | "Admin"
  },
  "requestId": "guid-uuid"
}
```

### Renovar tokens
```
POST /api/Auth/refresh-token
Body: { "refreshToken": "..." }
Response: { 
  "accessToken": "...",
  "refreshToken": "...",
  "accessTokenExpiresAt": "2026-01-04T20:00:00Z",
  "refreshTokenExpiresAt": "2026-02-03T20:00:00Z"
}
```

### Logout
```
POST /api/Auth/logout
Headers: { "Authorization": "Bearer {accessToken}" }
Body: { "refreshToken": "..." }
Response: { "message": "Logged out successfully" }
```

---

## ⚠️ IMPORTANTE

1. **Nunca guardes tokens en variables de estado de React** - siempre usa localStorage
2. **Siempre verifica la expiración** antes de usar el access token
3. **Renueva proactivamente** 5 minutos antes de que expire
4. **Limpia localStorage** si el refresh token expira o es inválido
5. **En producción**, considera usar httpOnly cookies en vez de localStorage (más seguro)

---

---

## 🎯 RESULTADO ESPERADO

Después de implementar estos cambios:
- ✅ La sesión se mantiene al recargar la página
- ✅ Los tokens se renuevan automáticamente
- ✅ El usuario no necesita volver a hacer login cada vez
- ✅ La sesión dura 30 días (duración del refresh token)
- ✅ El usuario puede cerrar y abrir el navegador sin perder la sesión
- ✅ El frontend siempre sabe si el usuario está logueado

---

## 📊 RESUMEN VISUAL DEL FLUJO

```
┌─────────────────────────────────────────────────────────────┐
│                    USUARIO HACE LOGIN                        │
└─────────────────────────────────────────────────────────────┘
                            ↓
        Frontend → POST /api/User/google-auth
                            ↓
        Backend → Devuelve: { token: "access|refresh", user: {...} }
                            ↓
        Frontend → token.split('|') → [accessToken, refreshToken]
                            ↓
        Frontend → localStorage.setItem('accessToken', ...)
        Frontend → localStorage.setItem('refreshToken', ...)
        Frontend → localStorage.setItem('user', ...)
                            ↓
                    ✅ USUARIO LOGUEADO

┌─────────────────────────────────────────────────────────────┐
│              USUARIO RECARGA LA PÁGINA (F5)                  │
└─────────────────────────────────────────────────────────────┘
                            ↓
        App carga → useEffect → checkAuthStatus()
                            ↓
        checkAuthStatus() → localStorage.getItem('accessToken')
        checkAuthStatus() → localStorage.getItem('refreshToken')
                            ↓
        ¿Hay tokens? → SÍ → Verificar expiración
                            ↓
        ¿Token válido? → SÍ → setIsAuthenticated(true)
                            ↓
                    ✅ USUARIO SIGUE LOGUEADO

        ¿Token válido? → NO → refreshAccessToken()
                            ↓
        POST /api/Auth/refresh-token → Recibe nuevos tokens
                            ↓
        Guarda nuevos tokens → setIsAuthenticated(true)
                            ↓
                    ✅ USUARIO SIGUE LOGUEADO

┌─────────────────────────────────────────────────────────────┐
│         USUARIO HACE PETICIÓN AL BACKEND                    │
└─────────────────────────────────────────────────────────────┘
                            ↓
        GET /api/SomeEndpoint
        Header: Authorization: Bearer {accessToken}
                            ↓
        ¿Token válido? → SÍ → ✅ Respuesta 200 OK
                            ↓
        ¿Token válido? → NO → ❌ Respuesta 401 Unauthorized
                            ↓
        Interceptor detecta 401 → Renueva token automáticamente
                            ↓
        Reintenta petición original → ✅ Respuesta 200 OK
```

---

## 🔑 CLAVES PARA QUE FUNCIONE

### 1. **localStorage es tu amigo**
```typescript
// ✅ CORRECTO: Guardar en localStorage
localStorage.setItem('accessToken', accessToken);
localStorage.setItem('refreshToken', refreshToken);

// ❌ INCORRECTO: Solo guardar en estado de React
const [token, setToken] = useState(accessToken); // Se pierde al recargar
```

### 2. **Verificar al cargar SIEMPRE**
```typescript
// ✅ CORRECTO: Verificar en useEffect
useEffect(() => {
  checkAuthStatus(); // Verifica tokens al cargar
}, []);

// ❌ INCORRECTO: Asumir que está logueado
const [isAuth] = useState(true); // No verifica nada
```

### 3. **Renovar automáticamente**
```typescript
// ✅ CORRECTO: Interceptor renueva automáticamente
axios.interceptors.response.use(
  response => response,
  async error => {
    if (error.response?.status === 401) {
      await refreshToken(); // Renueva automáticamente
      return axios(originalRequest); // Reintenta
    }
  }
);
```

### 4. **Manejar expiración**
```typescript
// ✅ CORRECTO: Verificar expiración
const expiresAt = localStorage.getItem('accessTokenExpiresAt');
if (new Date(expiresAt) < new Date()) {
  await refreshToken(); // Renovar antes de usar
}
```

---

## 🔍 CÓMO SABER SI EL USUARIO ESTÁ LOGUEADO (RESUMEN)

### Método 1: Verificar tokens en localStorage
```typescript
function isUserLoggedIn(): boolean {
  const accessToken = localStorage.getItem('accessToken');
  const refreshToken = localStorage.getItem('refreshToken');
  return !!(accessToken && refreshToken);
}
```

### Método 2: Verificar tokens Y expiración
```typescript
function isUserLoggedIn(): boolean {
  const accessToken = localStorage.getItem('accessToken');
  const refreshToken = localStorage.getItem('refreshToken');
  const expiresAt = localStorage.getItem('accessTokenExpiresAt');
  
  if (!accessToken || !refreshToken) {
    return false;
  }
  
  // Si el token expiró, aún está "logueado" pero necesita renovación
  if (expiresAt && new Date(expiresAt) < new Date()) {
    // Token expirado, pero el refresh token puede renovarlo
    return true; // Usuario técnicamente logueado, solo necesita renovar
  }
  
  return true;
}
```

### Método 3: Obtener información del usuario
```typescript
function getCurrentUser() {
  const userData = localStorage.getItem('user');
  if (userData) {
    return JSON.parse(userData);
  }
  return null;
}

// Uso:
const user = getCurrentUser();
if (user) {
  console.log('Usuario logueado:', user.name, user.email, user.role);
} else {
  console.log('Usuario NO logueado');
}
```

---

## 📱 EJEMPLO COMPLETO: Hook de React para Autenticación

```typescript
import { useState, useEffect } from 'react';

export function useAuth() {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [user, setUser] = useState(null);
  const [isLoading, setIsLoading] = useState(true);

  // ✅ Verificar autenticación al cargar
  useEffect(() => {
    checkAuth();
  }, []);

  async function checkAuth() {
    try {
      const accessToken = localStorage.getItem('accessToken');
      const refreshToken = localStorage.getItem('refreshToken');
      const userData = localStorage.getItem('user');

      if (!accessToken || !refreshToken) {
        setIsAuthenticated(false);
        setUser(null);
        setIsLoading(false);
        return;
      }

      // Verificar expiración
      const expiresAt = localStorage.getItem('accessTokenExpiresAt');
      if (expiresAt && new Date(expiresAt) < new Date()) {
        // Token expirado, renovar
        const renewed = await refreshAccessToken();
        if (!renewed) {
          setIsAuthenticated(false);
          setUser(null);
          setIsLoading(false);
          return;
        }
      }

      // Usuario autenticado
      setIsAuthenticated(true);
      if (userData) {
        setUser(JSON.parse(userData));
      }
    } catch (error) {
      console.error('Error verificando auth:', error);
      setIsAuthenticated(false);
      setUser(null);
    } finally {
      setIsLoading(false);
    }
  }

  async function login(googleToken: string) {
    try {
      const response = await fetch('http://localhost:7124/api/User/google-auth', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ accessToken: googleToken })
      });

      const data = await response.json();
      
      if (data.token) {
        const [accessToken, refreshToken] = data.token.split('|');
        localStorage.setItem('accessToken', accessToken);
        localStorage.setItem('refreshToken', refreshToken);
        localStorage.setItem('accessTokenExpiresAt', new Date(Date.now() + 60 * 60 * 1000).toISOString());
        localStorage.setItem('user', JSON.stringify(data.user));
        
        setIsAuthenticated(true);
        setUser(data.user);
        return { success: true };
      }
      
      throw new Error('Login failed');
    } catch (error) {
      console.error('Login error:', error);
      throw error;
    }
  }

  async function logout() {
    try {
      const refreshToken = localStorage.getItem('refreshToken');
      const accessToken = localStorage.getItem('accessToken');
      
      if (refreshToken && accessToken) {
        await fetch('http://localhost:7124/api/Auth/logout', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${accessToken}`
          },
          body: JSON.stringify({ refreshToken })
        });
      }
    } catch (error) {
      console.error('Logout error:', error);
    } finally {
      localStorage.clear();
      setIsAuthenticated(false);
      setUser(null);
    }
  }

  return {
    isAuthenticated,
    user,
    isLoading,
    login,
    logout,
    checkAuth
  };
}

// Uso en componentes:
function MyComponent() {
  const { isAuthenticated, user, isLoading, login, logout } = useAuth();

  if (isLoading) {
    return <div>Cargando...</div>;
  }

  if (!isAuthenticated) {
    return <button onClick={() => login(googleToken)}>Iniciar sesión</button>;
  }

  return (
    <div>
      <p>Hola, {user?.name}</p>
      <button onClick={logout}>Cerrar sesión</button>
    </div>
  );
}
```

---

---

## 🎯 FLUJO COMPLETO: Cómo funciona paso a paso

### 1. Usuario hace login por primera vez
```
Usuario → Click "Iniciar sesión con Google"
    ↓
Google OAuth → Devuelve JWT de Google
    ↓
Frontend → POST /api/User/google-auth con JWT de Google
    ↓
Backend → Valida JWT, crea/encuentra usuario, genera tokens
    ↓
Backend → Devuelve: { token: "accessToken|refreshToken", user: {...} }
    ↓
Frontend → Separa tokens: token.split('|')
    ↓
Frontend → Guarda en localStorage:
    - accessToken
    - refreshToken
    - accessTokenExpiresAt (1 hora desde ahora)
    - user (datos del usuario)
    ↓
✅ Usuario logueado - Estado: isAuthenticated = true
```

### 2. Usuario recarga la página (LO QUE FALTA IMPLEMENTAR)
```
Usuario → Recarga la página (F5)
    ↓
App se carga → useEffect ejecuta checkAuthStatus()
    ↓
checkAuthStatus() → Lee localStorage:
    - accessToken ✅ (existe)
    - refreshToken ✅ (existe)
    - accessTokenExpiresAt ✅ (existe)
    - user ✅ (existe)
    ↓
Verifica expiración:
    - expiresAt > now? → Token válido
    - expiresAt < now? → Token expirado, renovar
    ↓
Si token válido:
    ✅ setIsAuthenticated(true)
    ✅ setUser(JSON.parse(userData))
    ✅ Usuario sigue logueado
    ↓
Si token expirado:
    → refreshAccessToken()
    → POST /api/Auth/refresh-token
    → Recibe nuevos tokens
    → Guarda nuevos tokens
    ✅ setIsAuthenticated(true)
    ✅ Usuario sigue logueado
```

### 3. Usuario hace una petición al backend
```
Frontend → GET /api/SomeEndpoint
    ↓
Interceptor de Axios → Agrega header:
    Authorization: Bearer {accessToken}
    ↓
Backend → Valida token
    ↓
Si token válido:
    ✅ Devuelve datos (200 OK)
    ↓
Si token expirado (401):
    → Interceptor detecta 401
    → Renueva token automáticamente
    → Reintenta petición original
    ✅ Devuelve datos (200 OK)
```

### 4. Usuario cierra sesión
```
Usuario → Click "Cerrar sesión"
    ↓
Frontend → logout()
    ↓
Frontend → POST /api/Auth/logout (revoca refresh token)
    ↓
Frontend → localStorage.clear()
    ↓
Frontend → setIsAuthenticated(false)
    ↓
Frontend → Redirige a /login
    ↓
✅ Usuario deslogueado
```

---

## 🔍 CÓMO SABER SI EL USUARIO ESTÁ LOGUEADO EN CUALQUIER MOMENTO

### Opción 1: Función helper simple
```typescript
// authUtils.ts
export function isLoggedIn(): boolean {
  const accessToken = localStorage.getItem('accessToken');
  const refreshToken = localStorage.getItem('refreshToken');
  return !!(accessToken && refreshToken);
}

export function getCurrentUser() {
  const userData = localStorage.getItem('user');
  return userData ? JSON.parse(userData) : null;
}

// Uso en cualquier componente:
import { isLoggedIn, getCurrentUser } from './authUtils';

function MyComponent() {
  if (isLoggedIn()) {
    const user = getCurrentUser();
    return <div>Hola, {user.name}</div>;
  }
  return <div>No estás logueado</div>;
}
```

### Opción 2: Context de React (Recomendado para apps grandes)
```typescript
// AuthContext.tsx
import React, { createContext, useContext, useState, useEffect } from 'react';

interface AuthContextType {
  isAuthenticated: boolean;
  user: any | null;
  login: (googleToken: string) => Promise<void>;
  logout: () => Promise<void>;
  isLoading: boolean;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [isAuthenticated, setIsAuthenticated] = useState(false);
  const [user, setUser] = useState(null);
  const [isLoading, setIsLoading] = useState(true);

  // ✅ Verificar autenticación al cargar
  useEffect(() => {
    checkAuth();
  }, []);

  async function checkAuth() {
    const accessToken = localStorage.getItem('accessToken');
    const refreshToken = localStorage.getItem('refreshToken');
    const userData = localStorage.getItem('user');

    if (!accessToken || !refreshToken) {
      setIsAuthenticated(false);
      setUser(null);
      setIsLoading(false);
      return;
    }

    // Verificar expiración
    const expiresAt = localStorage.getItem('accessTokenExpiresAt');
    if (expiresAt && new Date(expiresAt) < new Date()) {
      const renewed = await refreshAccessToken();
      if (!renewed) {
        setIsAuthenticated(false);
        setUser(null);
        setIsLoading(false);
        return;
      }
    }

    setIsAuthenticated(true);
    if (userData) {
      setUser(JSON.parse(userData));
    }
    setIsLoading(false);
  }

  async function login(googleToken: string) {
    const response = await fetch('http://localhost:7124/api/User/google-auth', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ accessToken: googleToken })
    });

    const data = await response.json();
    if (data.token) {
      const [accessToken, refreshToken] = data.token.split('|');
      localStorage.setItem('accessToken', accessToken);
      localStorage.setItem('refreshToken', refreshToken);
      localStorage.setItem('accessTokenExpiresAt', new Date(Date.now() + 60 * 60 * 1000).toISOString());
      localStorage.setItem('user', JSON.stringify(data.user));
      
      setIsAuthenticated(true);
      setUser(data.user);
    }
  }

  async function logout() {
    const refreshToken = localStorage.getItem('refreshToken');
    const accessToken = localStorage.getItem('accessToken');
    
    if (refreshToken && accessToken) {
      await fetch('http://localhost:7124/api/Auth/logout', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Authorization': `Bearer ${accessToken}`
        },
        body: JSON.stringify({ refreshToken })
      });
    }
    
    localStorage.clear();
    setIsAuthenticated(false);
    setUser(null);
  }

  return (
    <AuthContext.Provider value={{ isAuthenticated, user, login, logout, isLoading }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth debe usarse dentro de AuthProvider');
  }
  return context;
}

// Uso en App.tsx:
function App() {
  return (
    <AuthProvider>
      <Router />
    </AuthProvider>
  );
}

// Uso en cualquier componente:
function Header() {
  const { isAuthenticated, user, logout } = useAuth();

  return (
    <header>
      {isAuthenticated ? (
        <div>
          <span>Hola, {user?.name}</span>
          <button onClick={logout}>Cerrar sesión</button>
        </div>
      ) : (
        <a href="/login">Iniciar sesión</a>
      )}
    </header>
  );
}
```

---

---

## ✅ CHECKLIST PARA EL FRONTEND

### Al hacer login:
- [ ] Recibir respuesta del backend con `token` y `user`
- [ ] Separar el token: `token.split('|')` → `[accessToken, refreshToken]`
- [ ] Guardar `accessToken` en localStorage
- [ ] Guardar `refreshToken` en localStorage
- [ ] Guardar `accessTokenExpiresAt` (1 hora desde ahora)
- [ ] Guardar `user` en localStorage (JSON.stringify)
- [ ] Actualizar estado de React: `setIsAuthenticated(true)`
- [ ] Actualizar estado de React: `setUser(user)`

### Al cargar la app (App.tsx o componente principal):
- [ ] En `useEffect`, llamar a `checkAuthStatus()`
- [ ] Leer `accessToken` de localStorage
- [ ] Leer `refreshToken` de localStorage
- [ ] Leer `user` de localStorage
- [ ] Si NO hay tokens → `setIsAuthenticated(false)`
- [ ] Si HAY tokens → verificar expiración
- [ ] Si token expirado → renovar automáticamente
- [ ] Si renovación exitosa → `setIsAuthenticated(true)`
- [ ] Si renovación falla → limpiar localStorage y redirigir a login

### En cada petición HTTP:
- [ ] Interceptor de Axios agrega: `Authorization: Bearer {accessToken}`
- [ ] Si respuesta es 401 → renovar token automáticamente
- [ ] Si renovación exitosa → reintentar petición original
- [ ] Si renovación falla → limpiar y redirigir a login

### Renovación proactiva:
- [ ] Verificar cada minuto si el token expira pronto (5 min antes)
- [ ] Si expira pronto → renovar automáticamente
- [ ] Guardar nuevos tokens en localStorage

### Al hacer logout:
- [ ] Llamar a `/api/Auth/logout` con refreshToken
- [ ] Limpiar localStorage completamente
- [ ] Actualizar estado: `setIsAuthenticated(false)`
- [ ] Actualizar estado: `setUser(null)`
- [ ] Redirigir a `/login`

### Para saber si el usuario está logueado:
- [ ] Función `isLoggedIn()`: verifica si hay tokens en localStorage
- [ ] Función `getCurrentUser()`: devuelve datos del usuario de localStorage
- [ ] Context de React: proporciona `isAuthenticated` y `user` a toda la app

---

## ⚠️ PUNTOS CRÍTICOS A RECORDAR

1. **SIEMPRE verifica tokens al cargar la app** - No asumas que el usuario está logueado
2. **SIEMPRE guarda tokens en localStorage** - No uses solo variables de estado de React
3. **SIEMPRE verifica la expiración** - El access token expira en 1 hora
4. **SIEMPRE renueva automáticamente** - Usa interceptors de Axios/Fetch
5. **SIEMPRE limpia localStorage al hacer logout** - No dejes tokens huérfanos
6. **SIEMPRE guarda datos del usuario** - Para saber quién está logueado sin hacer peticiones
7. **NUNCA confíes solo en el estado de React** - localStorage es la fuente de verdad
8. **SIEMPRE maneja errores de renovación** - Si falla, redirige a login

