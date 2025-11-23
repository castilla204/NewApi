# 🔐 FRONTEND - GUÍA COMPLETA DE MFA

## 📋 ¿A QUIÉN SE LE OBLIGA EL MFA (OTP)?

**MFA es OBLIGATORIO para:**
- ✅ **Admin** (role = 2) - Administradores del sistema
- ✅ **Expert** (role = 1) - Expertos que manejan dinero

**MFA es OPCIONAL para:**
- ⚠️ **Client** (role = 0) - Clientes normales

**Lógica del backend:**
```csharp
// Si el usuario es Admin o Expert → MFA obligatorio
var requiresMfa = userRole == UserRole.Admin || userRole == UserRole.Expert;
```

**¿Qué significa "obligatorio"?**
- Si un Admin o Expert NO tiene MFA habilitado → El backend bloquea TODAS las rutas (excepto las permitidas)
- Si un Admin o Expert tiene MFA habilitado pero NO lo ha verificado en esta sesión → El backend bloquea TODAS las rutas (excepto las permitidas)
- Los Clientes pueden usar la app sin MFA (es opcional para ellos)

---

# 🔐 FRONTEND - CÓMO VERIFICAR CÓDIGO MFA

## ⚠️ PROBLEMA ACTUAL

El endpoint `/api/auth/mfa/verify` devuelve **401 Unauthorized** porque:

1. **El token JWT no se está enviando** en el header `Authorization`
2. O el token ha expirado

## ✅ SOLUCIÓN

### 1. Asegúrate de enviar el token en TODAS las requests

```typescript
// Configurar axios para enviar token automáticamente
axios.interceptors.request.use(
  (config) => {
    const token = localStorage.getItem('accessToken');
    if (token) {
      config.headers.Authorization = `Bearer ${token}`;
    }
    return config;
  },
  (error) => Promise.reject(error)
);
```

### 2. Verificar código MFA

```typescript
async function verifyMfaCode(code: string) {
  try {
    const token = localStorage.getItem('accessToken');
    
    if (!token) {
      throw new Error('No access token found. Please login again.');
    }

    const response = await axios.post(
      'http://localhost:7124/api/auth/mfa/verify',
      {
        code: code,
        isRecoveryCode: false // true si es código de recuperación
      },
      {
        headers: {
          'Authorization': `Bearer ${token}`, // ✅ CRÍTICO: Enviar token
          'Content-Type': 'application/json'
        }
      }
    );

    // Si es exitoso, el backend devuelve nuevos tokens
    if (response.data.accessToken && response.data.refreshToken) {
      localStorage.setItem('accessToken', response.data.accessToken);
      localStorage.setItem('refreshToken', response.data.refreshToken);
      
      // Actualizar header de axios
      axios.defaults.headers.common['Authorization'] = `Bearer ${response.data.accessToken}`;
    }

    return {
      success: true,
      message: response.data.message
    };
  } catch (error: any) {
    if (error.response?.status === 401) {
      // Token inválido o expirado
      if (error.response?.data?.message?.includes('Invalid or missing user ID')) {
        // Token no válido - hacer refresh o logout
        console.error('Token inválido. Intentando renovar...');
        // Aquí deberías intentar renovar el token o hacer logout
      }
    }
    
    return {
      success: false,
      message: error.response?.data?.message || 'Error verifying MFA code'
    };
  }
}
```

### 3. Flujo completo cuando recibes 403 MFA_VERIFICATION_REQUIRED

```typescript
// Interceptor para capturar errores de MFA
axios.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config;

    // Si recibes 403 con MFA_VERIFICATION_REQUIRED
    if (error.response?.status === 403 && 
        error.response?.data?.error === 'MFA_VERIFICATION_REQUIRED') {
      
      // Mostrar pantalla de código MFA
      const mfaCode = await showMfaVerificationDialog();
      
      if (mfaCode) {
        // Verificar código
        const verifyResult = await verifyMfaCode(mfaCode);
        
        if (verifyResult.success) {
          // Reintentar request original con nuevo token
          originalRequest.headers['Authorization'] = 
            `Bearer ${localStorage.getItem('accessToken')}`;
          return axios(originalRequest);
        } else {
          // Código incorrecto
          throw new Error(verifyResult.message);
        }
      }
    }

    return Promise.reject(error);
  }
);
```

## 🔍 DEBUGGING

### Verificar que el token se está enviando:

1. Abre DevTools → Network
2. Busca la request a `/api/auth/mfa/verify`
3. Ve a la pestaña "Headers"
4. Busca "Request Headers" → "Authorization"
5. Debe decir: `Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6...`

### Si NO aparece el header Authorization:

- El token no se está guardando después del login
- O axios no está configurado para enviarlo automáticamente

### Si aparece pero sigue dando 401:

- El token ha expirado → Intentar renovar con refresh token
- El token es inválido → Hacer logout y login de nuevo

## 📝 CHECKLIST

- [ ] Token se guarda después del login
- [ ] Axios interceptor está configurado para enviar token
- [ ] Header `Authorization: Bearer <token>` se envía en la request
- [ ] Si token expira, se renueva automáticamente
- [ ] Si recibe 403 `MFA_VERIFICATION_REQUIRED`, muestra pantalla de código
- [ ] Después de verificar código, actualiza tokens y reintenta request

## 🚫 DESHABILITAR MFA

### Endpoint: `POST /api/auth/mfa/disable`

**Requisitos:**
- ✅ Requiere autenticación (JWT token)
- ✅ Requiere código TOTP de 6 dígitos
- ✅ **NO requiere contraseña** (todos los usuarios usan Google OAuth)

**Request:**
```typescript
async function disableMfa(totpCode: string) {
  try {
    const token = localStorage.getItem('accessToken');
    
    if (!token) {
      throw new Error('No access token found. Please login again.');
    }

    const response = await axios.post(
      'http://localhost:7124/api/auth/mfa/disable',
      {
        totpCode: totpCode  // Solo código TOTP de 6 dígitos
      },
      {
        headers: {
          'Authorization': `Bearer ${token}`,
          'Content-Type': 'application/json'
        }
      }
    );

    return {
      success: true,
      message: response.data.message // "MFA disabled successfully"
    };
  } catch (error: any) {
    if (error.response?.status === 400) {
      // Código TOTP incorrecto
      return {
        success: false,
        message: error.response?.data?.message || 'Invalid TOTP code'
      };
    }
    
    return {
      success: false,
      message: error.response?.data?.message || 'Error disabling MFA'
    };
  }
}
```

**Ejemplo de uso:**
```typescript
// Solo necesitas el código TOTP
const result = await disableMfa('123456');
```

**Respuestas:**
- ✅ **200 OK:** `{ "message": "MFA disabled successfully" }`
- ❌ **400 Bad Request:** `{ "message": "Invalid TOTP code" }`
- ❌ **401 Unauthorized:** Token inválido o expirado
- ❌ **500 Internal Server Error:** Error del servidor

**Nota importante:**
- ✅ **Solo requiere código TOTP** (no se necesita contraseña porque todos usan Google OAuth)
- El código TOTP es obligatorio (6 dígitos)
- Después de deshabilitar, el usuario ya no necesitará verificar código MFA en futuros logins

---

## 🎯 RESUMEN

**El backend ya está funcionando correctamente.** El problema es que el frontend no está enviando el token JWT en el header `Authorization`.

**Solución:** Configurar axios para enviar automáticamente el token en todas las requests.

**Endpoints MFA disponibles:**
- `GET /api/auth/mfa/status` - Ver estado de MFA
- `POST /api/auth/mfa/setup` - Configurar MFA (generar QR)
- `POST /api/auth/mfa/enable` - Habilitar MFA (confirmar con código)
- `POST /api/auth/mfa/verify` - Verificar código MFA
- `POST /api/auth/mfa/disable` - Deshabilitar MFA (requiere password + código TOTP)

