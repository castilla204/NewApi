# 🎨 GUÍA FRONTEND - REFRESH TOKENS

## ✅ YA IMPLEMENTADO EN BACKEND

El sistema de Refresh Tokens ya está completamente funcional. Solo falta actualizar el frontend.

---

## 🔐 CAMBIOS EN EL LOGIN

### ANTES (token único):
```typescript
const response = await api.post('/user/google-auth', {
  accessToken: googleToken
});

// Guardaba un solo token
localStorage.setItem('token', response.data.token);
```

### AHORA (access token + refresh token):
```typescript
const response = await api.post('/user/google-auth', {
  accessToken: googleToken
});

// El backend devuelve ambos tokens separados por "|"
const [accessToken, refreshToken] = response.data.token.split('|');

// Guardar ambos tokens
localStorage.setItem('accessToken', accessToken);
localStorage.setItem('refreshToken', refreshToken);

// ⚠️ IMPORTANTE: En producción usa httpOnly cookies en vez de localStorage
```

---

## 🔄 RENOVACIÓN AUTOMÁTICA DE TOKENS

### Configurar interceptor de Axios:

```typescript
import axios from 'axios';

// Configurar el token en cada request
axios.interceptors.request.use(
  config => {
    const token = localStorage.getItem('accessToken');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  error => Promise.reject(error)
);

// Renovar automáticamente si el token expiró
axios.interceptors.response.use(
  response => response,
  async error => {
    const originalRequest = error.config;

    // Si es 401 y no hemos reintentado aún
    if (error.response?.status === 401 && !originalRequest._retry) {
      originalRequest._retry = true;

      try {
        const refreshToken = localStorage.getItem('refreshToken');
        
        if (!refreshToken) {
          // No hay refresh token, ir a login
          localStorage.clear();
          window.location.href = '/login';
          return Promise.reject(error);
        }

        // Renovar tokens
        const response = await axios.post(
          'http://localhost:7124/api/auth/refresh-token',
          { refreshToken },
          { 
            _retry: true // Marcar para no intentar renovar este request
          }
        );

        const { accessToken, refreshToken: newRefreshToken } = response.data;

        // Guardar nuevos tokens
        localStorage.setItem('accessToken', accessToken);
        localStorage.setItem('refreshToken', newRefreshToken);

        // Reintentar request original con nuevo token
        originalRequest.headers.Authorization = `Bearer ${accessToken}`;
        return axios(originalRequest);
      } catch (refreshError) {
        // Refresh token también expiró - forzar re-login
        localStorage.clear();
        window.location.href = '/login';
        return Promise.reject(refreshError);
      }
    }

    return Promise.reject(error);
  }
);

export default axios;
```

---

## 🚪 LOGOUT SEGURO

### Implementar logout con revocación:

```typescript
const logout = async () => {
  try {
    const refreshToken = localStorage.getItem('refreshToken');
    const accessToken = localStorage.getItem('accessToken');

    if (accessToken && refreshToken) {
      // Revocar el refresh token en el backend
      await axios.post(
        'http://localhost:7124/api/auth/logout',
        { refreshToken },
        { headers: { Authorization: `Bearer ${accessToken}` }}
      );
    }
  } catch (error) {
    console.error('Logout error:', error);
    // Continuar con logout local aunque falle el backend
  } finally {
    // Limpiar tokens locales siempre
    localStorage.removeItem('accessToken');
    localStorage.removeItem('refreshToken');
    localStorage.removeItem('user');
    
    // Redirigir a login
    window.location.href = '/login';
  }
};
```

---

## 🔐 CERRAR TODAS LAS SESIONES

### Útil cuando el usuario sospecha que su cuenta fue comprometida:

```typescript
const revokeAllSessions = async () => {
  try {
    const accessToken = localStorage.getItem('accessToken');
    
    await axios.post(
      'http://localhost:7124/api/auth/revoke-all',
      {},
      { headers: { Authorization: `Bearer ${accessToken}` }}
    );
    
    alert('Todas las sesiones han sido cerradas. Por favor, inicia sesión de nuevo.');
    
    // Limpiar y redirigir
    localStorage.clear();
    window.location.href = '/login';
  } catch (error) {
    console.error('Error revocando sesiones:', error);
    alert('Error al cerrar sesiones. Por favor, contacta con soporte.');
  }
};
```

---

## 📱 EJEMPLO COMPLETO CON REACT

### Hook personalizado para autenticación:

```typescript
import { useState, useEffect } from 'react';
import axios from './axios'; // Axios configurado con interceptors

export const useAuth = () => {
  const [user, setUser] = useState(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    // Verificar si hay tokens al cargar
    const accessToken = localStorage.getItem('accessToken');
    const userData = localStorage.getItem('user');
    
    if (accessToken && userData) {
      setUser(JSON.parse(userData));
    }
    setLoading(false);
  }, []);

  const login = async (googleToken) => {
    try {
      const response = await axios.post('/user/google-auth', {
        accessToken: googleToken
      });

      // Separar tokens
      const [accessToken, refreshToken] = response.data.token.split('|');
      
      // Guardar tokens
      localStorage.setItem('accessToken', accessToken);
      localStorage.setItem('refreshToken', refreshToken);
      localStorage.setItem('user', JSON.stringify(response.data.user));
      
      setUser(response.data.user);
      return { success: true };
    } catch (error) {
      console.error('Login error:', error);
      return { success: false, error: error.message };
    }
  };

  const logout = async () => {
    try {
      const refreshToken = localStorage.getItem('refreshToken');
      const accessToken = localStorage.getItem('accessToken');

      if (accessToken && refreshToken) {
        await axios.post(
          '/api/auth/logout',
          { refreshToken },
          { headers: { Authorization: `Bearer ${accessToken}` }}
        );
      }
    } catch (error) {
      console.error('Logout error:', error);
    } finally {
      localStorage.clear();
      setUser(null);
      window.location.href = '/login';
    }
  };

  return {
    user,
    loading,
    login,
    logout,
    isAuthenticated: !!user
  };
};
```

### Uso en componentes:

```typescript
import { useAuth } from './hooks/useAuth';

function MyComponent() {
  const { user, logout, isAuthenticated } = useAuth();

  if (!isAuthenticated) {
    return <LoginPage />;
  }

  return (
    <div>
      <h1>Bienvenido, {user.name}</h1>
      <button onClick={logout}>Cerrar sesión</button>
    </div>
  );
}
```

---

## ⏱️ DURACIÓN DE TOKENS

| Token | Duración | Propósito |
|-------|----------|-----------|
| **Access Token** | 30 minutos | Acceso a recursos protegidos |
| **Refresh Token** | 7 días | Renovar access tokens |

**Flujo:**
1. Usuario se loguea → Recibe ambos tokens
2. Cada 30 minutos → Access token expira
3. Interceptor detecta 401 → Renueva con refresh token automáticamente
4. Usuario no nota nada → Experiencia fluida
5. Después de 7 días → Debe re-loguearse con Google

---

## 🔒 MEJORES PRÁCTICAS

### 1. ⚠️ En producción, usa httpOnly cookies

**Problema con localStorage:**
- Vulnerable a XSS (Cross-Site Scripting)
- JavaScript malicioso puede robar tokens

**Solución:**
```typescript
// Backend debe configurar cookies httpOnly
response.cookie('accessToken', accessToken, {
  httpOnly: true,  // No accesible desde JavaScript
  secure: true,    // Solo HTTPS
  sameSite: 'strict',
  maxAge: 30 * 60 * 1000 // 30 minutos
});
```

### 2. ✅ Verificar autenticación en cada página

```typescript
// En tu router/layout principal
useEffect(() => {
  const token = localStorage.getItem('accessToken');
  if (!token && !isPublicRoute()) {
    window.location.href = '/login';
  }
}, [location]);
```

### 3. 🔄 Mostrar estado de carga durante renovación

```typescript
const [isRefreshing, setIsRefreshing] = useState(false);

// En el interceptor
axios.interceptors.response.use(
  response => response,
  async error => {
    if (error.response?.status === 401) {
      setIsRefreshing(true);
      try {
        // ... renovar token
      } finally {
        setIsRefreshing(false);
      }
    }
  }
);
```

### 4. 🚨 Alertar al usuario antes de cerrar sesión

```typescript
// Alertar cuando el refresh token esté por expirar
useEffect(() => {
  const checkTokenExpiration = () => {
    const refreshToken = localStorage.getItem('refreshToken');
    if (!refreshToken) return;
    
    // Decodificar y verificar expiración (necesitarás jwt-decode)
    const decoded = jwtDecode(refreshToken);
    const expiresIn = decoded.exp * 1000 - Date.now();
    
    // Si expira en menos de 1 día
    if (expiresIn < 24 * 60 * 60 * 1000) {
      alert('Tu sesión expirará pronto. Por favor, inicia sesión de nuevo.');
    }
  };
  
  const interval = setInterval(checkTokenExpiration, 60 * 60 * 1000); // Cada hora
  return () => clearInterval(interval);
}, []);
```

---

## 🧪 TESTING

### Probar renovación manual:

```typescript
// En la consola del navegador
const refreshToken = localStorage.getItem('refreshToken');

fetch('http://localhost:7124/api/auth/refresh-token', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ refreshToken })
})
.then(res => res.json())
.then(data => {
  console.log('Nuevos tokens:', data);
  localStorage.setItem('accessToken', data.accessToken);
  localStorage.setItem('refreshToken', data.refreshToken);
});
```

---

## 📋 CHECKLIST DE IMPLEMENTACIÓN

Frontend debe:

- [ ] Separar tokens en login (split por `|`)
- [ ] Guardar `accessToken` y `refreshToken` por separado
- [ ] Configurar interceptor de Axios para renovación automática
- [ ] Implementar logout con revocación
- [ ] Actualizar todas las llamadas API para usar `accessToken`
- [ ] (Opcional) Implementar botón "Cerrar todas las sesiones"
- [ ] (Opcional) Alertar cuando el refresh token esté por expirar
- [ ] (Producción) Migrar a httpOnly cookies

---

## 🎯 ENDPOINTS DISPONIBLES

```
POST /api/user/google-auth           → Login (devuelve ambos tokens)
POST /api/auth/refresh-token         → Renovar access token
POST /api/auth/logout                → Cerrar sesión
POST /api/auth/revoke-all            → Cerrar todas las sesiones
```

---

## ❓ FAQ

**Q: ¿Por qué usar refresh tokens?**
A: Para permitir tokens de acceso de corta duración (30 min) sin forzar re-login constante.

**Q: ¿Qué pasa si alguien roba mi refresh token?**
A: Puedes usar `POST /api/auth/revoke-all` para invalidar todos los tokens. Además, el backend detecta reutilización y revoca automáticamente.

**Q: ¿Puedo tener múltiples sesiones activas?**
A: Sí, cada dispositivo/navegador tiene su propio refresh token.

**Q: ¿Cuándo debo re-loguearse?**
A: Después de 7 días de inactividad, o si cierra sesión manualmente.

---

## 🚀 ¡TODO LISTO!

El backend está completamente funcional. Solo necesitas actualizar el frontend siguiendo esta guía.

**Tiempo estimado:** 1-2 horas para implementar todo el frontend.

